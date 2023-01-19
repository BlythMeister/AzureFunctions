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
using System.Net.Cache;
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
                if (req.Query.ContainsKey("itemNo"))
                {
                    var listing = await GetListing(req.Query["itemNo"].ToString(), log);
                    return new OkObjectResult(listing);
                }

                var checkUser = Environment.GetEnvironmentVariable("CHECK_USER");
                var checkMax = Environment.GetEnvironmentVariable("CHECK_MAX");

                if (req.Query.ContainsKey("user"))
                {
                    checkUser = req.Query["user"].ToString();
                }

                if (req.Query.ContainsKey("results"))
                {
                    checkMax = req.Query["results"].ToString();
                }

                var listings = await GetListings(checkUser, checkMax, log);
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

        private static async Task<WebPage> LoadPage(string url, ILogger log)
        {
            if (url.Contains("?"))
            {
                url += $"&nocache={Guid.NewGuid()}";
            }
            else
            {
                url += $"?nocache={Guid.NewGuid()}";
            }

            var browser = new ScrapingBrowser
            {
                IgnoreCookies = true,
                Encoding = Encoding.UTF8,
                CachePolicy = new RequestCachePolicy(RequestCacheLevel.BypassCache),
                KeepAlive = false,
            };

            log.LogInformation($"Browser navigate to {url}");
            return await browser.NavigateToPageAsync(new Uri(url));
        }

        private static async Task<EbayListing> GetListing(string itemNumber, ILogger log)
        {
            var page = await LoadPage($"https://www.ebay.co.uk/itm/{itemNumber}", log);

            var imageLink = page.Find("meta", By.Name("twitter:image")).FirstOrDefault()?.Attributes["content"]?.Value; ;
            var title = page.Find("meta", By.Name("twitter:title")).FirstOrDefault()?.Attributes["content"]?.Value;

            var content = page.Find("div", By.Id("LeftSummaryPanel")).FirstOrDefault();

            string price, bids, buyItNow, timeLeft;

            if (content == null)
            {
                content = page.Find("div", By.Id("mainContent")).FirstOrDefault();

                price = ((HtmlTextNode)content.CssSelect("div.vi-price-np span.vi-VR-cvipPrice").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                bids = ((HtmlTextNode)content.CssSelect("div.vi-cvip-bidt1 a span").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                buyItNow = null;
                timeLeft = "-";
            }
            else
            {
                price = ((HtmlTextNode)content.CssSelect("div.x-price-primary span.ux-textspans").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                bids = ((HtmlTextNode)content.CssSelect("div.x-bid-count span.ux-textspans").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                buyItNow = ((HtmlTextNode)content.CssSelect("div.x-bin-action span.ux-call-to-action__text").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                timeLeft = ((HtmlTextNode)content.CssSelect("span.ux-timer__text").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
            }

            return new EbayListing(itemNumber, imageLink, title, bids, buyItNow, price, timeLeft);
        }

        private static async Task<List<EbayListing>> GetListings(string checkUser, string checkMax, ILogger log)
        {
            var items = new List<EbayListing>();

            checkMax ??= "100";

            var url = $"https://www.ebay.co.uk/sch/i.html?_sofindtype=0&_byseller=1&_fss=1&_fsradio=%26LH_SpecificSeller%3D1&_saslop=1&_sasl={checkUser}&_sop=1&_ipg={checkMax}&_dmd=1";
            log.LogInformation("Getting ebay listings from {url}", url);
            var page = await LoadPage(url, log);

            foreach (var itemNode in page.Find("div", By.Class("s-item__wrapper")))
            {
                log.LogDebug("Node: {node}", itemNode.ToString());

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
                var timeLeft = ((HtmlTextNode)itemNode.CssSelect("div.s-item__detail span.s-item__time-left").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;

                if (string.IsNullOrWhiteSpace(buyItNow))
                {
                    buyItNow = ((HtmlTextNode)itemNode.CssSelect("div.s-item__detail span.s-item__purchaseOptionsWithIcon").FirstOrDefault()?.ChildNodes.FirstOrDefault(x => x.NodeType == HtmlNodeType.Text))?.Text;
                }

                if (title != null && price != null)
                {
                    items.Add(new EbayListing(itemNumber, imageLink, title, bids, buyItNow, price, timeLeft));
                }
            }

            return items;
        }

        private static async Task SendAlerts(ILogger log)
        {
            var checkUser = Environment.GetEnvironmentVariable("CHECK_USER");
            var checkMax = Environment.GetEnvironmentVariable("CHECK_MAX");

            var listings = await GetListings(checkUser, checkMax, log);

            var blobsChanged = false;
            var newListingAlerts = await Blobs.ReadAppDataBlob<List<long>>("newAlert.dat", log);
            var bidAlerts = await Blobs.ReadAppDataBlob<Dictionary<long, (int, double)>>("bidAlert.dat", log);
            var endWithin1HourAlerts = await Blobs.ReadAppDataBlob<List<long>>("endsWithin1HourAlert.dat", log);
            var endWithin24HourAlerts = await Blobs.ReadAppDataBlob<List<long>>("endsWithin24HourAlert.dat", log);

            foreach (var ebayListing in listings)
            {
                if (await NotifyNewListings(log, newListingAlerts, ebayListing))
                {
                    blobsChanged = true;
                }

                if (await NotifyEndingSoon(log, endWithin24HourAlerts, ebayListing))
                {
                    blobsChanged = true;
                }

                if (await NotifyEndingReallySoon(log, endWithin1HourAlerts, ebayListing))
                {
                    blobsChanged = true;
                }

                if (await NotifyBid(log, bidAlerts, ebayListing))
                {
                    blobsChanged = true;
                }
            }

            if (await NotifyFinished(log, listings, newListingAlerts, bidAlerts, endWithin24HourAlerts, endWithin1HourAlerts))
            {
                blobsChanged = true;
            }

            if (blobsChanged)
            {
                await Blobs.WriteAppDataBlob(newListingAlerts, "newAlert.dat", log);
                await Blobs.WriteAppDataBlob(bidAlerts, "bidAlert.dat", log);
                await Blobs.WriteAppDataBlob(endWithin1HourAlerts, "endsWithin1HourAlert.dat", log);
                await Blobs.WriteAppDataBlob(endWithin24HourAlerts, "endsWithin24HourAlert.dat", log);
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
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_ENDING_24_HOUR"), out var notify) && notify)
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

        private static async Task<bool> NotifyEndingReallySoon(ILogger log, List<long> alerts, EbayListing ebayListing)
        {
            if (ebayListing.EndsWithin1Hour && !alerts.Contains(ebayListing.ListingNumber))
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_ENDING_1_HOUR"), out var notify) && notify)
                {
                    var emailSubject = $"eBay Listing 1 Hour Left - {ebayListing.Title}";
                    var emailHtml = $"<h1>eBay Listing 1 Hour Left<h1><h2>{ebayListing.Title}</h2><table><tr><td><img src=\"{ebayListing.ImageLink}\"/></td><td><p>Time Left: {ebayListing.TimeLeft}</p><p>Bids: {ebayListing.BidCount}</p><p>Price: {ebayListing.CurrentPrice:N}</p><td><tr></table><p><a href=\"{ebayListing.Link}\">View Listing</a></p>";
                    var emailText = $"eBay Listing 1 Hour Left\r\n\r\n{ebayListing.Title}\r\n\r\nTime Left: {ebayListing.TimeLeft}\r\nBids: {ebayListing.BidCount}\r\nPrice: {ebayListing.CurrentPrice:N}\r\nLink: {ebayListing.Link}";

                    await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                }

                alerts.Add(ebayListing.ListingNumber);
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
                var confirmedListing = await GetListing(ebayListing.ListingNumber.ToString(), log);

                if (!alerts[confirmedListing.ListingNumber].BidCount.Equals(confirmedListing.BidCount))
                {
                    if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_BID"), out var notify) && notify)
                    {
                        var emailSubject = $"eBay Listing Bid - {confirmedListing.Title}";
                        var emailHtml = $"<h1>eBay Listing Bid<h1><h2>{confirmedListing.Title}</h2><table><tr><td><img src=\"{confirmedListing.ImageLink}\"/></td><td><p>Time Left: {confirmedListing.TimeLeft}</p><p>Old Bids: {alerts[confirmedListing.ListingNumber].BidCount}</p><p>Old Price: £{alerts[confirmedListing.ListingNumber].CurrentPrice:N}</p><p>Bids: {confirmedListing.BidCount}</p><p>Price: £{confirmedListing.CurrentPrice:N}</p><td><tr></table><p><a href=\"{confirmedListing.Link}\">View Listing</a></p>";
                        var emailText = $"eBay Listing Bid\r\n\r\n{confirmedListing.Title}\r\n\r\nTime Left: {confirmedListing.TimeLeft}\r\nOld Bids: {alerts[confirmedListing.ListingNumber].BidCount}\r\nOld Price: £{alerts[confirmedListing.ListingNumber].CurrentPrice:N}\r\nBids: {confirmedListing.BidCount}\r\nPrice: £{confirmedListing.CurrentPrice:N}\r\nLink: {confirmedListing.Link}";

                        await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                    }

                    alerts[confirmedListing.ListingNumber] = (confirmedListing.BidCount, confirmedListing.CurrentPrice);
                    return true;
                }
            }

            return false;
        }

        private static async Task<bool> NotifyFinished(ILogger log, List<EbayListing> listings, List<long> newListingAlerts, Dictionary<long, (int, double)> bidAlerts, List<long> endWithin24HourAlerts, List<long> endWithin1HourAlerts)
        {
            var finished = new List<long>();

            foreach (var itemNumber in newListingAlerts.Where(x => !listings.Any(l => l.ListingNumber == x)))
            {
                if (!listings.Any())
                {
                    if (endWithin1HourAlerts.Contains(itemNumber) && endWithin24HourAlerts.Contains(itemNumber))
                    {
                        finished.Add(itemNumber);
                    }
                }
                else
                {
                    finished.Add(itemNumber);
                }
            }

            foreach (var itemNumber in finished)
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TYPE_ENDED"), out var notify) && notify)
                {
                    var listing = await GetListing(itemNumber.ToString(), log);

                    if (listing == null)
                    {
                        log.LogWarning("Unable to get final listing detail for {itemNumber}", itemNumber);
                        continue;
                    }

                    if (!listing.Ended)
                    {
                        log.LogWarning("Listing {itemNumber} appears to not be ended", itemNumber);
                        continue;
                    }

                    var (lastKnownBids, _) = bidAlerts[itemNumber];

                    var emailSubject = $"eBay Listing Ended - {listing.Title}";
                    string emailHtml, emailText;

                    if (listing.BidCount > 0)
                    {
                        if (listing.BidCount == 1 && lastKnownBids == 0)
                        {
                            if (!endWithin1HourAlerts.Contains(itemNumber))
                            {
                                emailHtml = $"<h1>eBay Listing Ended (Offer Accepted)<h1><h2>{listing.Title}</h2><table><tr><td><img src=\"{listing.ImageLink}\"/></td><td><p>Price: £{listing.CurrentPrice:N}</p><td><tr></table><p><a href=\"{listing.Link}\">View Listing</a></p>";
                                emailText = $"eBay Listing Ended (Offer Accepted)\r\n\r\n{listing.Title}\r\n\r\nPrice: £{listing.CurrentPrice:N}\r\nLink: {listing.Link}";
                            }
                            else
                            {
                                emailHtml = $"<h1>eBay Listing Ended (Sold - Possible Offer Accepted)<h1><h2>{listing.Title}</h2><table><tr><td><img src=\"{listing.ImageLink}\"/></td><td><p>Bids: {listing.BidCount}</p><p>Price: £{listing.CurrentPrice:N}</p><p>This is an offer accepted or 15 minute opening bid</p><td><tr></table><p><a href=\"{listing.Link}\">View Listing</a></p>";
                                emailText = $"eBay Listing Ended (Sold - Possible Offer Accepted)\r\n\r\n{listing.Title}\r\n\r\nBids: {listing.BidCount}\r\nPrice: £{listing.CurrentPrice:N}\r\nThis is an offer accepted or 15 minute opening bid\r\nLink: {listing.Link}";
                            }
                        }
                        else
                        {
                            emailHtml = $"<h1>eBay Listing Ended (Sold)<h1><h2>{listing.Title}</h2><table><tr><td><img src=\"{listing.ImageLink}\"/></td><td><p>Bids: {listing.BidCount}</p><p>Price: £{listing.CurrentPrice:N}</p><td><tr></table><p><a href=\"{listing.Link}\">View Listing</a></p>";
                            emailText = $"eBay Listing Ended (Sold)\r\n\r\n{listing.Title}\r\n\r\nBids: {listing.BidCount}\r\nPrice: £{listing.CurrentPrice:N}\r\nLink: {listing.Link}";
                        }
                    }
                    else
                    {
                        emailHtml = $"<h1>eBay Listing Ended (Unsold)<h1><h2>{listing.Title}</h2><table><tr><td><img src=\"{listing.ImageLink}\"/></td><td><p>No bids seen before ending</p><td><tr></table><p><a href=\"{listing.Link}\">View Listing</a></p>";
                        emailText = $"eBay Listing Ended (Unsold)\r\n\r\n{listing.Title}\r\nNo bids seen before ending.\r\n\r\nLink: {listing.Link}";
                    }

                    await Emails.SendEmail(emailSubject, emailText, emailHtml, log);
                }

                TryRemove(newListingAlerts, itemNumber);
                TryRemove(bidAlerts, itemNumber);
                TryRemove(endWithin24HourAlerts, itemNumber);
                TryRemove(endWithin1HourAlerts, itemNumber);
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
            public bool Ended { get; }
            public string TimeLeft { get; }
            public int BidCount { get; }
            public double CurrentPrice { get; }
            public bool BuyItNow { get; }

            public EbayListing(string itemNumber, string imageLink, string title, string bids, string buyItNow, string price, string timeLeft)
            {
                ListingNumber = long.Parse(itemNumber);
                ImageLink = imageLink;
                Link = $"https://www.ebay.co.uk/itm/{itemNumber}";
                Title = HttpUtility.HtmlDecode(title);
                BidCount = string.IsNullOrWhiteSpace(bids) ? 0 : int.Parse(bids.Replace("bids", "").Replace("bid", "").Trim());
                CurrentPrice = string.IsNullOrWhiteSpace(price) ? 0 : double.Parse(price.Replace("£", ""));
                EndsWithin24Hour = !string.IsNullOrWhiteSpace(timeLeft) && !timeLeft.Split(' ')[0].EndsWith("d");
                EndsWithin1Hour = EndsWithin24Hour && !string.IsNullOrWhiteSpace(timeLeft) && !timeLeft.Split(' ')[0].EndsWith("h");
                Ended = EndsWithin1Hour && !string.IsNullOrWhiteSpace(timeLeft) && timeLeft.Equals("-");
                TimeLeft = string.IsNullOrWhiteSpace(timeLeft) ? "No End Date" : timeLeft.Replace(" left", "");
                BuyItNow = !string.IsNullOrWhiteSpace(buyItNow);
            }
        }
    }
}
