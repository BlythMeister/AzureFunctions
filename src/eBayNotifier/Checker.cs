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
        public record EbayListing(long ListingNumber, string Link, string Title, string Description, DateTime EndTime);

        [FunctionName("eBayNotifierTimer")]
        public async Task RunTimer([TimerTrigger("0 */15 * * * *")] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation($"eBay Notifier Timer function executed at: {DateTime.Now}");
            try
            {
                var listings = await GetListings(log);
                await AlertNewListings(log, listings);
                await AlertEndingSoonListings(log, listings);
            }
            catch (Exception e)
            {
                log.LogError(e, "Error in check/email");
                await Emails.SendEmail("EBAY NOTIFIER ERROR", $"Error running eBay Notifier\r\n{e}", $"Error running eBay Notifier<br>{e}", true, log);
            }
        }

        [FunctionName("RunCheck")]
        public async Task<IActionResult> RunCheck([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation($"Run Check Web function executed at: {DateTime.Now}");
            try
            {
                var listings = await GetListings(log);
                await AlertNewListings(log, listings);
                await AlertEndingSoonListings(log, listings);
                return new OkObjectResult("Check Run");
            }
            catch (Exception e)
            {
                log.LogError(e, "Error running check");
                return new ExceptionResult(e, true);
            }
        }

        [FunctionName("GetCurrent")]
        public async Task<IActionResult> GetCurrentListingDetails([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation($"Get Current Web function executed at: {DateTime.Now}");
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
            using (var client = new HttpClient())
            {
                var content = await client.GetStringAsync("https://www.ebay.co.uk/dsc/i.html?_ssn=blyth_meister&_sop=1&_rss=1&_ipg=500");
                var rssFeed = XDocument.Parse(content);

                rssFeed.Descendants().Attributes().Where(x => x.IsNamespaceDeclaration).Remove();

                foreach (var elem in rssFeed.Descendants())
                {
                    elem.Name = elem.Name.LocalName;
                }

                foreach (var item in rssFeed.XPathSelectElements("//item"))
                {
                    var title = item.Element("title")?.Value ?? string.Empty;
                    var link = item.Element("link")?.Value ?? string.Empty;
                    var description = item.Element("description")?.Value ?? string.Empty;
                    var javaEndTime = item.Element("EndTime")?.Value ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(link) && !string.IsNullOrWhiteSpace(javaEndTime))
                    {
                        var itemNumberString = link.Substring(0, link.IndexOf('?', StringComparison.InvariantCultureIgnoreCase));
                        itemNumberString = itemNumberString.Substring(itemNumberString.LastIndexOf('/') + 1);

                        if (!long.TryParse(itemNumberString, out var itemNumber))
                        {
                            continue;
                        }

                        if (!long.TryParse(javaEndTime, out var javaTimestamp))
                        {
                            continue;
                        }

                        var endDate = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
                        endDate = endDate.AddMilliseconds(javaTimestamp).ToLocalTime();

                        items.Add(new EbayListing(itemNumber, link, title, description, endDate));
                    }
                }
            }

            return items;
        }

        private static async Task AlertNewListings(ILogger log, List<EbayListing> listings)
        {
            var notifiedNew = await Blobs.ReadAppDataBlob<List<long>>("newAlert.dat", log);
            var madeChanges = false;
            foreach (var ebayListing in listings)
            {
                if (!notifiedNew.Contains(ebayListing.ListingNumber))
                {
                    var emailSubject = $"New eBay Listing - {ebayListing.Title}";
                    var emailBody = $"<h1>New eBay Listing<h1><h2>Title: {ebayListing.Title}</h2><p>Ends: {ebayListing.EndTime:f}</p><p><a href=\"{ebayListing.Link}\">View Listing</a></p>{ebayListing.Description}";
                    var emailText = $"New eBay Listing\r\n\r\nTitle: {ebayListing.Title}\r\n\r\nEnds: {ebayListing.EndTime:f}\r\nLink: {ebayListing.Link}";

                    await Emails.SendEmail(emailSubject, emailText, emailBody, false, log);
                    await Emails.SendEmail(emailSubject, emailText, emailBody, true, log);

                    notifiedNew.Add(ebayListing.ListingNumber);
                    madeChanges = true;
                }
            }

            if (notifiedNew.Any(x => !listings.Any(l => l.ListingNumber == x)))
            {
                notifiedNew.RemoveAll(x => !listings.Any(l => l.ListingNumber == x));
                madeChanges = true;
            }

            if (madeChanges)
            {
                await Blobs.WriteAppDataBlob(notifiedNew, "newAlert.dat", log);
            }
        }

        private static async Task AlertEndingSoonListings(ILogger log, List<EbayListing> listings)
        {
            var notifiedEndingSoon = await Blobs.ReadAppDataBlob<List<long>>("endingSoonAlert.dat", log);
            var madeChanges = false;
            foreach (var ebayListing in listings.Where(x => x.EndTime < DateTime.Now.AddDays(-1)))
            {
                if (!notifiedEndingSoon.Contains(ebayListing.ListingNumber))
                {
                    var emailSubject = $"eBay Listing Ends Tomorrow - {ebayListing.Title}";
                    var emailBody = $"<h1>eBay Listing Ends Tomorrow<h1><h2>Title: {ebayListing.Title}</h2><p>Ends: {ebayListing.EndTime:f}</p><p><a href=\"{ebayListing.Link}\">View Listing</a></p>{ebayListing.Description}";
                    var emailText = $"eBay Listing Ends Tomorrow\r\n\r\nTitle: {ebayListing.Title}\r\n\r\nEnds: {ebayListing.EndTime:f}\r\nLink: {ebayListing.Link}";

                    await Emails.SendEmail(emailSubject, emailText, emailBody, false, log);
                    await Emails.SendEmail(emailSubject, emailText, emailBody, true, log);

                    notifiedEndingSoon.Add(ebayListing.ListingNumber);
                    madeChanges = true;
                }
            }

            if (notifiedEndingSoon.Any(x => !listings.Any(l => l.ListingNumber == x)))
            {
                notifiedEndingSoon.RemoveAll(x => !listings.Any(l => l.ListingNumber == x));
                madeChanges = true;
            }

            if (madeChanges)
            {
                await Blobs.WriteAppDataBlob(notifiedEndingSoon, "endingSoonAlert.dat", log);
            }
        }
    }
}
