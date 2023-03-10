using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;

namespace SwimmingPool
{
    public class PoolAvailable
    {
        [FunctionName("PoolAvailableTimer")]
        public static async Task RunTimer([TimerTrigger("0 */10 * * * *")] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation("Pool Available Timer function executed at: {Date}", DateTime.UtcNow);
            try
            {
                if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TIMER"), out var notify) && notify)
                {
                    await NotifyAvailableDates(log);
                }
                else
                {
                    log.LogWarning("Timer checker is off");
                }
            }
            catch (Exception e)
            {
                log.LogError(e, "Error in check/email");
                await Emails.SendEmail("POOL CHECK ERROR", $"Error running pool check:\r\n{e}", log);
            }
        }

        [FunctionName("RunCheck")]
        public static async Task<IActionResult> RunCheck([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation("Run Check Web function executed at: {Date}", DateTime.UtcNow);
            try
            {
                await NotifyAvailableDates(log);
                return new OkObjectResult("Check Run");
            }
            catch (Exception e)
            {
                log.LogError(e, "Error running check");
                return new ExceptionResult(e, true);
            }
        }

        [FunctionName("AllPoolAvailable")]
        public static async Task<IActionResult> GetAllPoolAvailable([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation("Pool All Available Web function executed at: {Date}", DateTime.UtcNow);
            try
            {
                var result = (await GetGoodDates(log)).ToList();
                return new OkObjectResult(result.Select(x => x.Good ? $"GOOD - {x.Date:f}" : $"BAD - {x.Date:f}"));
            }
            catch (Exception e)
            {
                log.LogError(e, "Error running check");
                return new ExceptionResult(e, true);
            }
        }

        [FunctionName("GoodPoolAvailable")]
        public static async Task<IActionResult> GetGoodPoolAvailable([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation("Pool Good Available Web function executed at: {Date}", DateTime.UtcNow);
            try
            {
                var result = (await GetGoodDates(log)).ToList();
                return new OkObjectResult(result.Where(x => x.Good).Select(x => x.Date.ToString("f")));
            }
            catch (Exception e)
            {
                log.LogError(e, "Error running check");
                return new ExceptionResult(e, true);
            }
        }

        private static async Task NotifyAvailableDates(ILogger log)
        {
            var result = (await GetGoodDates(log)).ToList();

            var sentDates = await Blobs.ReadAppDataBlob<List<DateTime>>("data.dat", log);

            if (sentDates.Any(x => x < DateTime.UtcNow))
            {
                sentDates.RemoveAll(x => x < DateTime.UtcNow);
                await Blobs.WriteAppDataBlob(sentDates, "data.dat", log);
            }

            result.RemoveAll(x => sentDates.Contains(x.Date));

            if (result.Any())
            {
                var message = new StringBuilder();

                var goodDates = result.Where(x => x.Good).Select(x => x.Date).ToList();
                var badDates = result.Where(x => !x.Good).Select(x => x.Date).ToList();

                message.AppendLine($"There are {goodDates.Count} new good and {badDates.Count} new bad swimming slots:");

                if (result.Any(x => x.Good))
                {
                    message.AppendLine("Good slots:");
                    foreach (var goodDate in goodDates)
                    {
                        message.AppendLine($" - {goodDate:f}");
                    }
                }

                if (result.Any(x => !x.Good))
                {
                    message.AppendLine($"Bad slots:");
                    foreach (var badDate in badDates)
                    {
                        message.AppendLine($" - {badDate:f}");
                    }
                }

                message.AppendLine($"Click to book: {Environment.GetEnvironmentVariable("POOL_URL_BOOKINGS")}");

                await Emails.SendEmail($"{result.Count} New Pool Dates Available", message.ToString(), log);

                sentDates.AddRange(result.Select(x => x.Date));
                await Blobs.WriteAppDataBlob(sentDates, "data.dat", log);
            }
        }

        private static async Task<IEnumerable<(DateTime Date, bool Good)>> GetGoodDates(ILogger log)
        {
            var dates = await GetPoolAvailableDateTimes(log);
            var goodBadDates = new List<(DateTime Date, bool Good)>();

            foreach (var date in dates)
            {
                var goodDate = false;
                log.LogInformation("Checking {date:f}", date);
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    if (date.TimeOfDay >= TimeSpan.Parse("10:00") && date.TimeOfDay <= TimeSpan.Parse("14:30"))
                    {
                        goodDate = true;
                    }
                }

                if (goodDate)
                {
                    log.LogInformation("Good Date :) - {date:f}", date);
                    goodBadDates.Add((date, true));
                }
                else
                {
                    log.LogInformation("Bad Date :( - {date:f}", date);
                    goodBadDates.Add((date, false));
                }
            }

            return goodBadDates;
        }

        private static async Task<List<DateTime>> GetPoolAvailableDateTimes(ILogger log)
        {
            using var client = new HttpClient();

            var content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("action","dopbsp_calendar_schedule_get"),
                new("dopbsp_frontend_ajax_request","true"),
                new("id","1"),
                new("year",Environment.GetEnvironmentVariable("POOL_CHECK_YEAR")),
                new("firstYear","\"false\""),
            });

            client.DefaultRequestHeaders.Add("Origin", Environment.GetEnvironmentVariable("POOL_URL_BASE"));
            client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
            client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
            client.DefaultRequestHeaders.Add("Referer", Environment.GetEnvironmentVariable("POOL_URL_BOOKINGS"));

            var sessions = new List<DateTime>();

            log.LogInformation("Calling for available");
            var result = await client.PostAsync(Environment.GetEnvironmentVariable("POOL_URL_POST"), content);
            log.LogInformation("Got result status {Status}", result.StatusCode);
            if (result.IsSuccessStatusCode)
            {
                var resultContent = await result.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(resultContent))
                {
                    throw new Exception("No content");
                }

                var obj = JObject.Parse(resultContent);

                foreach (var dateItem in obj.Children().Select(x => x.ToObject<JProperty>()))
                {
                    var date = dateItem.Name;
                    var dateO = JObject.Parse(dateItem.Value.ToObject<JValue>().Value.ToString());
                    foreach (var hourItem in dateO.Value<JObject>("hours").Children().Select(x => x.ToObject<JProperty>()))
                    {
                        var hour = hourItem.Name;
                        var hourO = JObject.Parse(hourItem.Value.ToString());
                        var available = hourO.Value<string>("available");
                        if (available != "0" && DateTime.TryParse($"{date}T{hour}", out var parsedDate) && parsedDate >= DateTime.UtcNow)
                        {
                            sessions.Add(parsedDate);
                        }
                    }
                }
            }

            if (!sessions.Any())
            {
                await Emails.SendEmail("POOL CHECK WARNING", "Found no dates when checking", log);
            }

            return sessions;
        }
    }
}
