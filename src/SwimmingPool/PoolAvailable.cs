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
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Threading.Tasks;
using System.Web.Http;

namespace SwimmingPool
{
    public class PoolAvailable
    {
        [FunctionName("PoolAvailableTimer")]
        public static async Task RunTimer([TimerTrigger("0 */20 * * * *", RunOnStartup = true)] TimerInfo myTimer, ILogger log)
        {
            log.LogInformation("Pool Available Timer function executed at: {Date}", DateTime.UtcNow);

            if (bool.TryParse(Environment.GetEnvironmentVariable("NOTIFY_TIMER"), out var notify) && notify)
            {
                var attempt = 0;
                while (attempt < 10)
                {
                    attempt++;
                    try
                    {
                        log.LogInformation("Notify dates attempt {Attempt}", attempt);
                        await NotifyAvailableDates(log);
                        break;
                    }
                    catch (Exception e) when (attempt >= 7)
                    {
                        log.LogError(e, "Error in check/email");
                        await Emails.SendEmail("POOL CHECK ERROR", $"Error running pool check:\r\n{e}", log);
                        break;
                    }
                    catch (Exception e)
                    {
                        var exponent = Math.Pow(2, attempt);
                        var delay = TimeSpan.FromSeconds(10 * exponent);
                        log.LogWarning(e, "Error in check/email on attempt {Attempt}, will retry in {Delay} seconds", attempt, delay.TotalSeconds);
                        await Task.Delay(delay);
                    }
                }
            }
            else
            {
                log.LogWarning("Timer checker is off");
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

        [FunctionName("RawJson")]
        public static async Task<IActionResult> GetRawJson([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation("Raw JSON Web function executed at: {Date}", DateTime.UtcNow);
            try
            {
                var result = await GetRawData(log);
                return new OkObjectResult(result);
            }
            catch (Exception e)
            {
                log.LogError(e, "Error running function");
                return new ExceptionResult(e, true);
            }
        }

        [FunctionName("AllPool")]
        public static async Task<IActionResult> GetAllPool([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation("Pool All Web function executed at: {Date}", DateTime.UtcNow);
            try
            {
                var result = await GetPoolDateTimes(log);
                return new OkObjectResult(result.Select(x => x.Available ? $"AVAILABLE - {x.Slot:f}" : $"NOT AVAILABLE - {x.Slot:f}"));
            }
            catch (Exception e)
            {
                log.LogError(e, "Error running function");
                return new ExceptionResult(e, true);
            }
        }

        [FunctionName("AllPoolAvailable")]
        public static async Task<IActionResult> GetAllPoolAvailable([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation("Pool All Available Web function executed at: {Date}", DateTime.UtcNow);
            try
            {
                var result = (await GetGoodBadAvailableDates(log, false)).ToList();
                return new OkObjectResult(result.Select(x => x.Good ? $"GOOD - {x.Date:f}" : $"BAD - {x.Date:f}"));
            }
            catch (Exception e)
            {
                log.LogError(e, "Error running function");
                return new ExceptionResult(e, true);
            }
        }

        [FunctionName("GoodPoolAvailable")]
        public static async Task<IActionResult> GetGoodPoolAvailable([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)] HttpRequest req, ILogger log)
        {
            log.LogInformation("Pool Good Available Web function executed at: {Date}", DateTime.UtcNow);
            try
            {
                var result = (await GetGoodBadAvailableDates(log, false)).ToList();
                return new OkObjectResult(result.Where(x => x.Good).Select(x => x.Date.ToString("f")));
            }
            catch (Exception e)
            {
                log.LogError(e, "Error running function");
                return new ExceptionResult(e, true);
            }
        }

        private static async Task NotifyAvailableDates(ILogger log)
        {
            var result = (await GetGoodBadAvailableDates(log, true)).ToList();

            result.RemoveAll(x => x.Date.Date <= DateTime.UtcNow.Date);

            var sentDates = await Blobs.ReadAppDataBlob<List<DateTime>>("data.dat", log);

            if (sentDates.Any(x => x.Date.Date <= DateTime.UtcNow.Date))
            {
                sentDates.RemoveAll(x => x.Date.Date <= DateTime.UtcNow.Date);
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

                await Emails.SendEmail($"{result.Count} New Pool Dates Available", message.ToString(), log);

                sentDates.AddRange(result.Select(x => x.Date));
                await Blobs.WriteAppDataBlob(sentDates, "data.dat", log);
            }
        }

        private static async Task<IEnumerable<(DateTime Date, bool Good)>> GetGoodBadAvailableDates(ILogger log, bool notifyNoDates)
        {
            var dates = await GetPoolDateTimes(log);
            var goodBadDates = new List<(DateTime Date, bool Good)>();

            if (!dates.Any())
            {
                if (notifyNoDates)
                {
                    await Emails.SendEmail("POOL CHECK WARNING", "Found no dates when checking", log);
                }
            }
            else
            {
                foreach (var date in dates.Where(x => x.Available).Select(x => x.Slot))
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
                        log.LogInformation("Good Date - {date:f}", date);
                        goodBadDates.Add((date, true));
                    }
                    else
                    {
                        log.LogInformation("Bad Date - {date:f}", date);
                        goodBadDates.Add((date, false));
                    }
                }
            }

            return goodBadDates;
        }

        private static async Task<string> GetRawData(ILogger log)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                SslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls,
            };

            using var client = new HttpClient(handler)
            {
                DefaultRequestHeaders =
                {
                    {"Origin", Environment.GetEnvironmentVariable("POOL_URL_BASE") },
                    {"Sec-Fetch-Site", "same-origin" },
                    {"Sec-Fetch-Mode", "cors" },
                    {"Sec-Fetch-Dest", "empty" },
                    {"Referer", Environment.GetEnvironmentVariable("POOL_URL_BOOKINGS") },
                    {"Cache-Control", "no-cache" },
                    {"Accept", "*/*" },
                    { "Accept-Encoding", "gzip, deflate, br" }
                },
                Timeout = TimeSpan.FromMinutes(1)
            };

            var content = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
                {
                    new("action", "dopbsp_calendar_schedule_get"),
                    new("dopbsp_frontend_ajax_request", "true"),
                    new("id", "1"),
                    new("year", Environment.GetEnvironmentVariable("POOL_CHECK_YEAR")),
                    new("firstYear", "\"false\""),
                });

            log.LogInformation("Calling for available");
            var result = await client.PostAsync(Environment.GetEnvironmentVariable("POOL_URL_POST"), content);
            log.LogInformation("Got result status {Status}", result.StatusCode);
            string resultContent = null;
            try
            {
                resultContent = await result.Content.ReadAsStringAsync();
            }
            catch (Exception e) when (result.IsSuccessStatusCode)
            {
                throw new Exception("No content on success status", e);
            }

            if (result.IsSuccessStatusCode)
            {
                if (string.IsNullOrWhiteSpace(resultContent))
                {
                    throw new Exception("No content on success status");
                }

                return resultContent;
            }

            if (string.IsNullOrEmpty(resultContent))
            {
                throw new Exception($"Non Success Status Code {result.StatusCode} ({(int)result.StatusCode})");
            }

            throw new Exception($"Non Success Status Code {result.StatusCode} ({(int)result.StatusCode})\nContent:{resultContent}");
        }

        private static async Task<List<(DateTime Slot, bool Available)>> GetPoolDateTimes(ILogger log)
        {
            var sessions = new List<(DateTime Slot, bool Available)>();

            var resultContent = await GetRawData(log);

            var obj = JObject.Parse(resultContent);

            foreach (var dateItem in obj.Children<JProperty>())
            {
                var date = dateItem.Name;
                var dateO = JObject.Parse(dateItem.Value.ToObject<JValue>().Value.ToString());
                foreach (var hourItem in dateO.Value<JObject>("hours").Children<JProperty>())
                {
                    var hour = hourItem.Name;
                    var hourO = JObject.Parse(hourItem.Value.ToString());
                    var available = hourO.Value<string>("available");
                    if (DateTime.TryParse($"{date}T{hour}", out var parsedDate))
                    {
                        sessions.Add((parsedDate, available != "0"));
                    }
                }
            }

            return sessions;
        }
    }
}
