using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Twilio;
using Twilio.Rest.Api.V2010.Account;

namespace Sios.DurableAlarm
{
    public static class Functions
    {
        [FunctionName("orchestrator")]
        public static async Task<bool> RunOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContext context, ILogger log)
        {

            int TWILIO_RETRY_COUNT = int.Parse(Environment.GetEnvironmentVariable("TWILIO_RETRY_COUNT"));

            bool authorized = false;


            for (int i = 0; i < TWILIO_RETRY_COUNT; i++)
            {
                await context.CallActivityAsync(
                    "gatherCode",
                    context.InstanceId);

                using (var timeoutCts = new CancellationTokenSource())
                {
                    // The user has 90 seconds to respond with the code they received in the SMS message.
                    DateTime expiration = context.CurrentUtcDateTime.AddSeconds(90);
                    Task timeoutTask = context.CreateTimer(expiration, timeoutCts.Token);

                    for (int retryCount = 0; retryCount <= 3; retryCount++)
                    {
                        Task<bool> challengeResponseTask =
                            context.WaitForExternalEvent<bool>("Approval");

                        Task winner = await Task.WhenAny(challengeResponseTask, timeoutTask);
                        if (winner == challengeResponseTask)
                        {


                            log.LogInformation("success");
                            log.LogInformation("code:" + challengeResponseTask.Result);
                            if (challengeResponseTask.Result)
                            {
                                authorized = true;
                                break;
                            }


                        }
                        else
                        {
                            // Timeout expired
                            break;
                        }
                    }

                    if (!timeoutTask.IsCompleted)
                    {
                        // All pending timers must be complete or canceled before the function exits.
                        timeoutCts.Cancel();
                    }

                    if (authorized) break;

                }

            }


            return authorized;

        }

        [FunctionName("gatherCode")]
        public static void GatherCode([ActivityTrigger] string name, string instanceId, ILogger log)
        {
            log.LogInformation($"Saying hello to {name}.");

            string TWILIO_GATHER_CODE_VOICE_URL = Environment.GetEnvironmentVariable("TWILIO_GATHER_CODE_VOICE_URL");
            int TWILIO_MAX_CODE_DIGIT = int.Parse(Environment.GetEnvironmentVariable("TWILIO_MAX_CODE_DIGIT"));
            string TWILIO_FROM_PHONE_NUMBER = Environment.GetEnvironmentVariable("TWILIO_FROM_PHONE_NUMBER");
            string TWILIO_TO_PHONE_NUMBER = Environment.GetEnvironmentVariable("TWILIO_TO_PHONE_NUMBER");

            // CHECK_CODE関数のURLを取得をアプリケーション環境変数から取得する。
            string check_code_func_url = Environment.GetEnvironmentVariable("TWILIO_CHECK_CODE_FUNC_URL");

            // コードを生成する
            string[] numbers = { "ぜろ", "いち", "に", "さん", "よん", "ご", "ろく", "なな", "はち", "きゅう", "じゅう" };
            string strPwdchar = "0123456789";
            string requiredCode = "";
            string requiredCodeYomi = "";
            Random rnd = new Random();
            for (int i = 0; i < TWILIO_MAX_CODE_DIGIT; i++)
            {
                int iRandom = rnd.Next(0, strPwdchar.Length - 1);
                requiredCode += strPwdchar.Substring(iRandom, 1);
                requiredCodeYomi += numbers[iRandom];

            }


            string accountSid = Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID"); // Your Account SID from www.twilio.com/console
            string authToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN"); ;   // Your Auth Token from www.twilio.com/console

            TwilioClient.Init(accountSid, authToken);

            string twiml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
              + "<Response>"
              + "  <Gather timeout=\"10\" finishOnKey=\"#\" action=\"" + check_code_func_url + "&amp;instanceid=" + instanceId + "&amp;requiredcode=" + requiredCode + "\" method=\"POST\">"
              //                  + "  <Gather timeout="10" finishOnKey="#">"
              + "    <Play>" + TWILIO_GATHER_CODE_VOICE_URL + "</Play>"
              + "    <Say language=\"ja-jp\" voice=\"alice\">" + requiredCodeYomi + "</Say>"
              + "  </Gather>"
              + "</Response>";

            var call = CallResource.Create(
            url: new Uri("http://twimlets.com/echo?Twiml=" + HttpUtility.UrlEncode(twiml)),
            to: new Twilio.Types.PhoneNumber(TWILIO_TO_PHONE_NUMBER),
            from: new Twilio.Types.PhoneNumber(TWILIO_FROM_PHONE_NUMBER)
        );
        }

        [FunctionName("checkCode")]
        public static async Task<IActionResult> CheckCode(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")]HttpRequest req, 
            [OrchestrationClient]DurableOrchestrationClient client,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string TWILIO_OK_VOICE_URL = Environment.GetEnvironmentVariable("TWILIO_OK_VOICE_URL");
            string TWILIO_NG_VOICE_URL = Environment.GetEnvironmentVariable("TWILIO_NG_VOICE_URL");

            string instanceid = req.Query["instanceid"];
            string requiredCode = req.Query["requiredcode"];

            string code = req.Form["Digits"];

            log.LogInformation("code:" + code);
            log.LogInformation("instanceid:" + instanceid);

            bool result = false;
            string twiml = "";
            if (code == requiredCode)
            {
                result = true;
                twiml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<Response>"
                + "   <Play>" + TWILIO_OK_VOICE_URL + "</Play>"
                + "</Response>";

            }
            else
            {
                result = false;
                twiml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                + "<Response>"
                + "   <Play>" + TWILIO_NG_VOICE_URL + "</Play>"
                + "</Response>";
            }


            await client.RaiseEventAsync(instanceid, "Approval", result);

            return new ContentResult { Content = twiml, ContentType = "application/xml" };

        }

        [FunctionName("orchestratorClient")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")]HttpRequestMessage req,
            [OrchestrationClient]DurableOrchestrationClient starter,
            ILogger log)
        {
            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("orchestrator", null);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }
}