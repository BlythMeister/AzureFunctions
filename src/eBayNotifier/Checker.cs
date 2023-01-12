using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using System.Xml.Linq;
using System.Xml.XPath;

namespace eBayNotifier
{
    public class Checker
    {
        public record EbayListing(long ListingNumber, string Link, string Title, string Description, DateTime EndTime, int BidCount, double CurrentPrice);

        [FunctionName("eBayNotifierTimer")]
        public static async Task RunTimer([TimerTrigger("0 */10 * * * *")] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation("eBay Notifier Timer function executed at: {Date}", DateTime.UtcNow);
            try
            {
                await SendAlerts(log);
            }
            catch (Exception e)
            {
                log.LogError(e, "Error in check/email");
                await Emails.SendErrorEmail("EBAY NOTIFIER ERROR", $"Error running eBay Notifier\r\n{e}", log);
            }
        }

        [FunctionName("RunCheck")]
        public static async Task<IActionResult> RunCheck([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation("Run Check Web function executed at: {Date}", DateTime.UtcNow);
            try
            {
                await SendAlerts(log);
                return new OkObjectResult("Check Run");
            }
            catch (Exception e)
            {
                log.LogError(e, "Error running check");
                return new ExceptionResult(e, true);
            }
        }

        [FunctionName("GetCurrent")]
        public static async Task<IActionResult> GetCurrentListingDetails([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation("Get Current Web function executed at: {Date}", DateTime.UtcNow);
            try
            {
                var listings = await GetListings(log);
                return new OkObjectResult(listings);
            }
            catch (Exception e)
            {
                log.LogError(e, "Error running check");
                return new ExceptionResult(e, true);
            }
        }

        private static async Task<List<EbayListing>> GetListings(ILogger log)
        {
            var items = new List<EbayListing>();
            using var client = new HttpClient();

            var checkUser = Environment.GetEnvironmentVariable("CHECK_USER");
            var checkMax = Environment.GetEnvironmentVariable("CHECK_MAX");

            var url = $"https://www.ebay.co.uk/dsc/i.html?_ssn={checkUser}&_sop=1&_rss=1&_ipg={checkMax}";
            log.LogInformation("Getting ebay listings from {url}", url);
            var content = await client.GetStringAsync(url);
            log.LogTrace("Response: {content}", content);
            var rssFeed = XDocument.Parse(content);

            rssFeed.Descendants().Attributes().Where(x => x.IsNamespaceDeclaration).Remove();

            foreach (var elem in rssFeed.Descendants())
            {
                elem.Name = elem.Name.LocalName;
            }

            var itemNodes = rssFeed.XPathSelectElements("//item");
            log.LogInformation("There are {count} item nodes", itemNodes.Count());

            foreach (var itemNode in itemNodes)
            {
                log.LogTrace("Node: {node}", itemNode.ToString());
                var title = itemNode.Element("title")?.Value ?? string.Empty;
                var link = itemNode.Element("link")?.Value ?? string.Empty;
                var description = itemNode.Element("description")?.Value ?? string.Empty;
                var javaEndTime = itemNode.Element("EndTime")?.Value ?? string.Empty;
                var currentPriceText = itemNode.Element("CurrentPrice")?.Value ?? string.Empty;
                var bidCountText = itemNode.Element("BidCount")?.Value ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(link) && !string.IsNullOrWhiteSpace(javaEndTime))
                {
                    var itemNumberString = link.Substring(0, link.IndexOf('?', StringComparison.InvariantCultureIgnoreCase));
                    itemNumberString = itemNumberString.Substring(itemNumberString.LastIndexOf('/') + 1);

                    if (!long.TryParse(itemNumberString, out var itemNumber))
                    {
                        log.LogWarning("Unable to parse '{data}' (itemNumber from url {link}) to long", itemNumberString, link);
                        continue;
                    }

                    if (!long.TryParse(javaEndTime, out var javaTimestamp))
                    {
                        log.LogWarning("Unable to parse '{data}' (EndTime) to long", javaTimestamp);
                        continue;
                    }

                    if (!double.TryParse(currentPriceText, out var currentPrice))
                    {
                        log.LogWarning("Unable to parse '{data}' (CurrentPrice) to double", currentPriceText);
                        continue;
                    }

                    if (!int.TryParse(bidCountText, out var bidCount))
                    {
                        log.LogWarning("Unable to parse '{data}' (BidCount) to int", bidCountText);
                        continue;
                    }

                    var endDate = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
                    endDate = endDate.AddMilliseconds(javaTimestamp);

                    items.Add(new EbayListing(itemNumber, link, title, description, endDate, bidCount, (currentPrice / 100)));
                }
            }

            return items;
        }

        private static async Task SendAlerts(ILogger log)
        {
            var listings = await GetListings(log);

            var blobsChanged = false;
            var newListingAlerts = await Blobs.ReadAppDataBlob<List<long>>("newAlert.dat", log);
            var endingSoonAlerts = await Blobs.ReadAppDataBlob<List<long>>("endingSoonAlert.dat", log);
            var titleChangeAlerts = await Blobs.ReadAppDataBlob<Dictionary<long, string>>("titleChangeAlert.dat", log);
            var bidAlerts = await Blobs.ReadAppDataBlob<Dictionary<long, (int, double)>>("bidAlert.dat", log);
            var expectedEndDates = await Blobs.ReadAppDataBlob<Dictionary<long, DateTime>>("expectedEndDates.dat", log);

            foreach (var ebayListing in listings)
            {
                if (!expectedEndDates.ContainsKey(ebayListing.ListingNumber))
                {
                    expectedEndDates.Add(ebayListing.ListingNumber, ebayListing.EndTime);
                    blobsChanged = true;
                }

                if (await NotifyNewListings(log, newListingAlerts, ebayListing))
                {
                    blobsChanged = true;
                }

                if (await NotifyEndingSoon(log, endingSoonAlerts, ebayListing))
                {
                    blobsChanged = true;
                }

                if (await NotifyTitleChange(log, titleChangeAlerts, ebayListing))
                {
                    blobsChanged = true;
                }

                if (await NotifyBid(log, bidAlerts, ebayListing))
                {
                    blobsChanged = true;
                }
            }

            if (await NotifyFinished(log, listings, expectedEndDates, newListingAlerts, endingSoonAlerts, titleChangeAlerts, bidAlerts))
            {
                blobsChanged = true;
            }

            if (blobsChanged)
            {
                await Blobs.WriteAppDataBlob(newListingAlerts, "newAlert.dat", log);
                await Blobs.WriteAppDataBlob(endingSoonAlerts, "endingSoonAlert.dat", log);
                await Blobs.WriteAppDataBlob(titleChangeAlerts, "titleChangeAlert.dat", log);
                await Blobs.WriteAppDataBlob(bidAlerts, "bidAlert.dat", log);
                await Blobs.WriteAppDataBlob(expectedEndDates, "expectedEndDates.dat", log);
            }
        }

        private static async Task<bool> NotifyNewListings(ILogger log, List<long> alerts, EbayListing ebayListing)
        {
            if (!alerts.Contains(ebayListing.ListingNumber))
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_NEW_LISTING"), out var notify) && notify)
                {
                    var emailSubject = $"New eBay Listing - {ebayListing.Title}";
                    var emailHtml = $"<h1>New eBay Listing<h1><h2>Title: {ebayListing.Title}</h2><p>Ends: {ebayListing.EndTime:f}</p><p><a href=\"{ebayListing.Link}\">View Listing</a></p>{ebayListing.Description}";
                    var emailText = $"New eBay Listing\r\n\r\nTitle: {ebayListing.Title}\r\n\r\nEnds: {ebayListing.EndTime:f}\r\nLink: {ebayListing.Link}";

                    await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                }

                alerts.Add(ebayListing.ListingNumber);
                return true;
            }

            return false;
        }

        private static async Task<bool> NotifyEndingSoon(ILogger log, List<long> alerts, EbayListing ebayListing)
        {
            if (ebayListing.EndTime < DateTime.UtcNow.AddDays(1) && !alerts.Contains(ebayListing.ListingNumber))
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_ENDING_SOON"), out var notify) && notify)
                {
                    var emailSubject = $"eBay Listing Ends Tomorrow - {ebayListing.Title}";
                    var emailHtml = $"<h1>eBay Listing Ends Tomorrow<h1><h2>Title: {ebayListing.Title}</h2><p>Ends: {ebayListing.EndTime:f}</p><p>Bids: {ebayListing.BidCount}</p><p>Price: {ebayListing.CurrentPrice:N}</p><p><a href=\"{ebayListing.Link}\">View Listing</a></p>{ebayListing.Description}";
                    var emailText = $"eBay Listing Ends Tomorrow\r\n\r\nTitle: {ebayListing.Title}\r\n\r\nEnds: {ebayListing.EndTime:f}\r\nBids: {ebayListing.BidCount}\r\nPrice: {ebayListing.CurrentPrice:N}\r\nLink: {ebayListing.Link}";

                    await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                }

                alerts.Add(ebayListing.ListingNumber);
                return true;
            }

            return false;
        }

        private static async Task<bool> NotifyTitleChange(ILogger log, Dictionary<long, string> alerts, EbayListing ebayListing)
        {
            if (!alerts.ContainsKey(ebayListing.ListingNumber))
            {
                alerts.Add(ebayListing.ListingNumber, ebayListing.Title);
                return true;
            }

            if (!alerts[ebayListing.ListingNumber].Equals(ebayListing.Title))
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_TITLE_CHANGE"), out var notify) && notify)
                {
                    var emailSubject = $"eBay Listing Title Update - {ebayListing.Title}";
                    var emailHtml = $"<h1>eBay Listing Title Update<h1><h2>Title: {ebayListing.Title}</h2><p>Old Title: {alerts[ebayListing.ListingNumber]}</p><p><a href=\"{ebayListing.Link}\">View Listing</a></p>{ebayListing.Description}";
                    var emailText = $"eBay Listing Title Update\r\n\r\nTitle: {ebayListing.Title}\r\n\r\nOld Title: {alerts[ebayListing.ListingNumber]}\r\nLink: {ebayListing.Link}";

                    await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                }

                alerts[ebayListing.ListingNumber] = ebayListing.Title;
                return true;
            }

            return false;
        }

        private static async Task<bool> NotifyBid(ILogger log, Dictionary<long, (int BidCount, double CurrentPrice)> alerts, EbayListing ebayListing)
        {
            if (!alerts.ContainsKey(ebayListing.ListingNumber))
            {
                alerts.Add(ebayListing.ListingNumber, (ebayListing.BidCount, ebayListing.CurrentPrice));
                return true;
            }

            if (!alerts[ebayListing.ListingNumber].BidCount.Equals(ebayListing.BidCount))
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_BID"), out var notify) && notify)
                {
                    var emailSubject = $"eBay Listing New Bid - {ebayListing.Title}";
                    var emailHtml = $"<h1>eBay Listing New Bid<h1><h2>Title: {ebayListing.Title}</h2><p>Ends: {ebayListing.EndTime:f}</p><p>Old Bids: {alerts[ebayListing.ListingNumber].BidCount}</p><p>Old Price: {alerts[ebayListing.ListingNumber].CurrentPrice:N}</p><p>Bids: {ebayListing.BidCount}</p><p>Price: {ebayListing.CurrentPrice:N}</p><p><a href=\"{ebayListing.Link}\">View Listing</a></p>{ebayListing.Description}";
                    var emailText = $"eBay Listing New Bid\r\n\r\nTitle: {ebayListing.Title}\r\n\r\nEnds: {ebayListing.EndTime:f}\r\nOld Bids: {alerts[ebayListing.ListingNumber].BidCount}\r\nOld Price: {alerts[ebayListing.ListingNumber].CurrentPrice:N}\r\nBids: {ebayListing.BidCount}\r\nPrice: {ebayListing.CurrentPrice:N}\r\nLink: {ebayListing.Link}";

                    await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                }

                alerts[ebayListing.ListingNumber] = (ebayListing.BidCount, ebayListing.CurrentPrice);
                return true;
            }

            return false;
        }

        private static async Task<bool> NotifyFinished(ILogger log, List<EbayListing> listings, Dictionary<long, DateTime> expectedEndDates, List<long> newListingAlerts, List<long> endingSoonAlerts, Dictionary<long, string> titleChangeAlerts, Dictionary<long, (int, double)> bidAlerts)
        {
            var finished = new List<long>();

            foreach (var (itemNumber, endDate) in expectedEndDates.Where(x => !listings.Any(l => l.ListingNumber == x.Key)))
            {
                if (!listings.Any())
                {
                    if (endDate <= DateTime.UtcNow)
                    {
                        finished.Add(itemNumber);
                    }
                }
                else
                {
                    finished.Add(itemNumber);
                }
            }

            foreach (var listing in finished)
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_ENDED"), out var notify) && notify)
                {
                    if (titleChangeAlerts.ContainsKey(listing) && bidAlerts.ContainsKey(listing))
                    {
                        var lastKnownTitle = titleChangeAlerts[listing];
                        var (lastKnownBid, lastKnownPrice) = bidAlerts[listing];

                        var emailSubject = $"eBay Listing Ended - {lastKnownTitle}";
                        string emailHtml, emailText;

                        if (lastKnownBid > 0)
                        {
                            emailHtml = $"<h1>eBay Listing Ended<h1><h2>Title: {lastKnownTitle}</h2><p>Last Known Bids: {lastKnownBid}</p><p>Last Known Selling Price: {lastKnownPrice:N}</p><p><a href=\"https://www.ebay.co.uk/itm/{listing}\">View Listing</a></p>";
                            emailText = $"eBay Listing Ended\r\n\r\nTitle: {lastKnownTitle}\r\n\r\nLast Known Bids: {lastKnownBid}\r\nLast Known Selling Price: {lastKnownPrice:N}\r\nLink: https://www.ebay.co.uk/itm/{listing}";
                        }
                        else
                        {
                            emailHtml = $"<h1>eBay Listing Ended<h1><h2>Title: {lastKnownTitle}</h2><p>No bids seen before ending.  Possible unsold, accecpted offer or listing cancellation</p><p><a href=\"https://www.ebay.co.uk/itm/{listing}\">View Listing</a></p>";
                            emailText = $"eBay Listing Ended\r\n\r\nTitle: {lastKnownTitle}\r\nNo bids seen before ending.  Possible unsold, accepted offer or listing cancellation\r\n\r\nLink: https://www.ebay.co.uk/itm/{listing}";
                        }

                        await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                    }
                }

                TryRemove(newListingAlerts, listing);
                TryRemove(endingSoonAlerts, listing);
                TryRemove(titleChangeAlerts, listing);
                TryRemove(bidAlerts, listing);
                TryRemove(expectedEndDates, listing);
            }

            return finished.Any();
        }

        private static void TryRemove(List<long> items, long item)
        {
            if (items.Contains(item))
            {
                items.Remove(item);
            }
        }

        private static void TryRemove<T>(Dictionary<long, T> items, long item)
        {
            if (items.ContainsKey(item))
            {
                items.Remove(item);
            }
        }
    }
}
