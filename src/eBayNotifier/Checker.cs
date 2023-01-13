using HtmlAgilityPack;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using ScrapySharp.Extensions;
using ScrapySharp.Html;
using ScrapySharp.Network;
using Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;

namespace eBayNotifier
{
    public class Checker
    {
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

        [FunctionName("GetFile")]
        public static async Task<IActionResult> GetFile([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation("Get File Web function executed at: {Date}", DateTime.UtcNow);
            try
            {
                var name = $"{req.Query["name"]}.dat";
                log.LogInformation("Loading file: {file}", name);
                var content = await Blobs.ReadAppDataBlobRaw(name, log);
                return new OkObjectResult(content);
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

            var checkUser = Environment.GetEnvironmentVariable("CHECK_USER");
            var checkMax = Environment.GetEnvironmentVariable("CHECK_MAX") ?? "50";

            var url = $"https://www.ebay.co.uk/sch/i.html?_sofindtype=0&_byseller=1&_fss=1&_fsradio=%26LH_SpecificSeller%3D1&_saslop=1&_sasl={checkUser}&_sop=1&_ipg={checkMax}&_dmd=1";
            log.LogInformation("Getting ebay listings from {url}", url);

            var browser = new ScrapingBrowser
            {
                IgnoreCookies = true,
                Encoding = Encoding.UTF8
            };
            var page = await browser.NavigateToPageAsync(new Uri(url));

            foreach (var itemNode in page.Find("div", By.Class("s-item__wrapper")))
            {
                log.LogTrace("Node: {node}", itemNode.ToString());

                var itemLink = itemNode.CssSelect("div.s-item__image a").FirstOrDefault()?.Attributes["href"]?.Value;

                var itemNumber = itemLink.Substring(0, itemLink.IndexOf('?'));
                itemNumber = itemNumber.Substring(itemNumber.LastIndexOf('/') + 1);

                if (string.IsNullOrWhiteSpace(itemNumber) || itemNumber == "123456")
                {
                    continue;
                }

                var imageLink = itemNode.CssSelect("div.s-item__image-wrapper img.s-item__image-img").FirstOrDefault()?.Attributes["src"]?.Value;
                var title = ((HtmlTextNode)itemNode.CssSelect("div.s-item__title span").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                var price = ((HtmlTextNode)itemNode.CssSelect("div.s-item__detail span.s-item__price").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                var bids = ((HtmlTextNode)itemNode.CssSelect("div.s-item__detail span.s-item__bids").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                var buyItNow = ((HtmlTextNode)itemNode.CssSelect("div.s-item__detail span.s-item__buyItNowOption").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                var purchaseOption = ((HtmlTextNode)itemNode.CssSelect("div.s-item__detail span.s-item__purchaseOptionsWithIcon").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                var timeLeft = ((HtmlTextNode)itemNode.CssSelect("div.s-item__detail span.s-item__time-left").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;

                if (title != null && price != null)
                {
                    items.Add(new EbayListing(itemNumber, imageLink, title, bids, buyItNow, purchaseOption, price, timeLeft));
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
            var endWithin1Hour = await Blobs.ReadAppDataBlob<List<long>>("endsWithin1Hour.dat", log);
            var endWithin24Hour = await Blobs.ReadAppDataBlob<List<long>>("endsWithin24Hour.dat", log);
            var imageLinks = await Blobs.ReadAppDataBlob<Dictionary<long, string>>("imageLinks.dat", log);

            foreach (var ebayListing in listings)
            {
                if (!endWithin1Hour.Contains(ebayListing.ListingNumber) && ebayListing.EndsWithin1Hour)
                {
                    endWithin1Hour.Add(ebayListing.ListingNumber);
                    blobsChanged = true;
                }

                if (!endWithin24Hour.Contains(ebayListing.ListingNumber) && ebayListing.EndsWithin24Hour)
                {
                    endWithin24Hour.Add(ebayListing.ListingNumber);
                    blobsChanged = true;
                }

                if (!imageLinks.ContainsKey(ebayListing.ListingNumber) && !string.IsNullOrWhiteSpace(ebayListing.ImageLink))
                {
                    imageLinks.Add(ebayListing.ListingNumber, ebayListing.ImageLink);
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

            if (await NotifyFinished(log, listings, endWithin1Hour, endWithin24Hour, imageLinks, newListingAlerts, endingSoonAlerts, titleChangeAlerts, bidAlerts))
            {
                blobsChanged = true;
            }

            if (blobsChanged)
            {
                await Blobs.WriteAppDataBlob(newListingAlerts, "newAlert.dat", log);
                await Blobs.WriteAppDataBlob(endingSoonAlerts, "endingSoonAlert.dat", log);
                await Blobs.WriteAppDataBlob(titleChangeAlerts, "titleChangeAlert.dat", log);
                await Blobs.WriteAppDataBlob(bidAlerts, "bidAlert.dat", log);
                await Blobs.WriteAppDataBlob(endWithin1Hour, "endsWithin1Hour.dat", log);
                await Blobs.WriteAppDataBlob(endWithin24Hour, "endsWithin24Hour.dat", log);
                await Blobs.WriteAppDataBlob(imageLinks, "imageLinks.dat", log);
            }
        }

        private static async Task<bool> NotifyNewListings(ILogger log, List<long> alerts, EbayListing ebayListing)
        {
            if (!alerts.Contains(ebayListing.ListingNumber))
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_NEW_LISTING"), out var notify) && notify)
                {
                    var emailSubject = $"New eBay Listing - {ebayListing.Title}";
                    var emailHtml = $"<h1>New eBay Listing<h1><h2>{ebayListing.Title}</h2><table><tr><td><img src=\"{ebayListing.ImageLink}\"/></td><td><p>Time Left: {ebayListing.TimeLeft}</p><p>Buy It Now: {ebayListing.BuyItNow}</p><p>Price: £{ebayListing.CurrentPrice:N}</p><td><tr></table><p><a href=\"{ebayListing.Link}\">View Listing</a></p>";
                    var emailText = $"New eBay Listing\r\n\r\n{ebayListing.Title}\r\n\r\nTime Left: {ebayListing.TimeLeft}\r\nBuy It Now: {ebayListing.BuyItNow}\r\nPrice: £{ebayListing.CurrentPrice:N}\r\nLink: {ebayListing.Link}";

                    await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                }

                alerts.Add(ebayListing.ListingNumber);
                return true;
            }

            return false;
        }

        private static async Task<bool> NotifyEndingSoon(ILogger log, List<long> alerts, EbayListing ebayListing)
        {
            if (ebayListing.EndsWithin24Hour && !alerts.Contains(ebayListing.ListingNumber))
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_ENDING_SOON"), out var notify) && notify)
                {
                    var emailSubject = $"eBay Listing Ends Tomorrow - {ebayListing.Title}";
                    var emailHtml = $"<h1>eBay Listing Ends Tomorrow<h1><h2>{ebayListing.Title}</h2><table><tr><td><img src=\"{ebayListing.ImageLink}\"/></td><td><p>Time Left: {ebayListing.TimeLeft}</p><p>Bids: {ebayListing.BidCount}</p><p>Price: {ebayListing.CurrentPrice:N}</p><td><tr></table><p><a href=\"{ebayListing.Link}\">View Listing</a></p>";
                    var emailText = $"eBay Listing Ends Tomorrow\r\n\r\n{ebayListing.Title}\r\n\r\nTime Left: {ebayListing.TimeLeft}\r\nBids: {ebayListing.BidCount}\r\nPrice: {ebayListing.CurrentPrice:N}\r\nLink: {ebayListing.Link}";

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
                    var emailHtml = $"<h1>eBay Listing Title Update<h1><h2>{ebayListing.Title}</h2><table><tr><td><img src=\"{ebayListing.ImageLink}\"/></td><td><p>Old {alerts[ebayListing.ListingNumber]}</p><td><tr></table><p><a href=\"{ebayListing.Link}\">View Listing</a></p>";
                    var emailText = $"eBay Listing Title Update\r\n\r\n{ebayListing.Title}\r\n\r\nOld {alerts[ebayListing.ListingNumber]}\r\nLink: {ebayListing.Link}";

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
                    var emailSubject = $"eBay Listing Bid - {ebayListing.Title}";
                    var emailHtml = $"<h1>eBay Listing Bid<h1><h2>{ebayListing.Title}</h2><table><tr><td><img src=\"{ebayListing.ImageLink}\"/></td><td><p>Time Left: {ebayListing.TimeLeft}</p><p>Old Bids: {alerts[ebayListing.ListingNumber].BidCount}</p><p>Old Price: £{alerts[ebayListing.ListingNumber].CurrentPrice:N}</p><p>Bids: {ebayListing.BidCount}</p><p>Price: £{ebayListing.CurrentPrice:N}</p><td><tr></table><p><a href=\"{ebayListing.Link}\">View Listing</a></p>";
                    var emailText = $"eBay Listing Bid\r\n\r\n{ebayListing.Title}\r\n\r\nTime Left: {ebayListing.TimeLeft}\r\nOld Bids: {alerts[ebayListing.ListingNumber].BidCount}\r\nOld Price: £{alerts[ebayListing.ListingNumber].CurrentPrice:N}\r\nBids: {ebayListing.BidCount}\r\nPrice: £{ebayListing.CurrentPrice:N}\r\nLink: {ebayListing.Link}";

                    await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                }

                alerts[ebayListing.ListingNumber] = (ebayListing.BidCount, ebayListing.CurrentPrice);
                return true;
            }

            return false;
        }

        private static async Task<bool> NotifyFinished(ILogger log, List<EbayListing> listings, List<long> endWithin1Hour, List<long> endWithin24Hour, Dictionary<long, string> imageLinks, List<long> newListingAlerts, List<long> endingSoonAlerts, Dictionary<long, string> titleChangeAlerts, Dictionary<long, (int, double)> bidAlerts)
        {
            var finished = new List<long>();

            foreach (var itemNumber in newListingAlerts.Where(x => !listings.Any(l => l.ListingNumber == x)))
            {
                if (!listings.Any())
                {
                    if (endWithin1Hour.Contains(itemNumber) && endWithin24Hour.Contains(itemNumber))
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
                        var lastKnownImage = imageLinks[listing];
                        var lastKnownTitle = titleChangeAlerts[listing];
                        var (lastKnownBid, lastKnownPrice) = bidAlerts[listing];

                        var emailSubject = $"eBay Listing Ended - {lastKnownTitle}";
                        string emailHtml, emailText;

                        if (lastKnownBid > 0)
                        {
                            emailHtml = $"<h1>eBay Listing Ended<h1><h2>{lastKnownTitle}</h2><table><tr><td><img src=\"{lastKnownImage}\"/></td><td><p>Last Known Bids: {lastKnownBid}</p><p>Last Known Selling Price: £{lastKnownPrice:N}</p><td><tr></table><p><a href=\"https://www.ebay.co.uk/itm/{listing}\">View Listing</a></p>";
                            emailText = $"eBay Listing Ended\r\n\r\n{lastKnownTitle}\r\n\r\nLast Known Bids: {lastKnownBid}\r\nLast Known Selling Price: £{lastKnownPrice:N}\r\nLink: https://www.ebay.co.uk/itm/{listing}";
                        }
                        else
                        {
                            emailHtml = $"<h1>eBay Listing Ended<h1><h2>{lastKnownTitle}</h2><table><tr><td><img src=\"{lastKnownImage}\"/></td><td><p>No bids seen before ending.  Possible unsold, accepted offer or listing cancellation</p><td><tr></table><p><a href=\"https://www.ebay.co.uk/itm/{listing}\">View Listing</a></p>";
                            emailText = $"eBay Listing Ended\r\n\r\n{lastKnownTitle}\r\nNo bids seen before ending.  Possible unsold, accepted offer or listing cancellation\r\n\r\nLink: https://www.ebay.co.uk/itm/{listing}";
                        }

                        await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                    }
                }

                TryRemove(endWithin1Hour, listing);
                TryRemove(endWithin24Hour, listing);
                TryRemove(imageLinks, listing);
                TryRemove(newListingAlerts, listing);
                TryRemove(endingSoonAlerts, listing);
                TryRemove(titleChangeAlerts, listing);
                TryRemove(bidAlerts, listing);
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

        public class EbayListing
        {
            public long ListingNumber { get; }
            public string ImageLink { get; }
            public string Link { get; }
            public string Title { get; }
            public bool EndsWithin24Hour { get; }
            public bool EndsWithin1Hour { get; }
            public string TimeLeft { get; }
            public int BidCount { get; }
            public double CurrentPrice { get; }
            public bool BuyItNow { get; }

            public EbayListing(string itemNumber, string imageLink, string title, string bids, string buyItNow, string purchaseOption, string price, string timeLeft)
            {
                ListingNumber = long.Parse(itemNumber);
                ImageLink = imageLink;
                Link = $"https://www.ebay.co.uk/itm/{itemNumber}";
                Title = HttpUtility.HtmlDecode(title);
                BidCount = string.IsNullOrWhiteSpace(bids) ? 0 : int.Parse(bids.Replace("bids", "").Replace("bid", "").Trim());
                CurrentPrice = double.Parse(price.Replace("£", ""));
                EndsWithin24Hour = !string.IsNullOrWhiteSpace(timeLeft) && !timeLeft.Split(' ')[0].EndsWith("d");
                EndsWithin1Hour = EndsWithin24Hour && !string.IsNullOrWhiteSpace(timeLeft) && !timeLeft.Split(' ')[0].EndsWith("h");
                TimeLeft = string.IsNullOrWhiteSpace(timeLeft) ? "No End Date" : timeLeft.Replace(" left", "");
                BuyItNow = !string.IsNullOrWhiteSpace(buyItNow) || (!string.IsNullOrWhiteSpace(purchaseOption) && purchaseOption.Contains("Buy it now"));
            }
        }
    }
}
