using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.EventGrid;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Producer.EventGrid
{
    public class Functions
    {
        [FunctionName(nameof(PostToEventGrid))]
        public async Task<IActionResult> PostToEventGrid(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest request,
            [DurableClient] IDurableOrchestrationClient client,
            ILogger log)
        {
            var inputObject = JObject.Parse(await request.ReadAsStringAsync());
            var numberOfMessages = inputObject.Value<int>(@"NumberOfMessages");

            var workTime = -1;
            if (inputObject.TryGetValue(@"WorkTime", out var workTimeVal))
            {
                workTime = workTimeVal.Value<int>();
            }

            var testRunId = Guid.NewGuid().ToString();
            var orchId = await client.StartNewAsync(nameof(GenerateMessagesForEventGrid),
                    Tuple.Create(numberOfMessages, testRunId, workTime));

            log.LogTrace($@"Kicked off {numberOfMessages} message creation...");

            return await client.WaitForCompletionOrCreateCheckStatusResponseAsync(request, orchId, TimeSpan.FromMinutes(2));
        }

        [FunctionName(nameof(GenerateMessagesForEventGrid))]
        public async Task<JObject> GenerateMessagesForEventGrid(
            [OrchestrationTrigger] IDurableOrchestrationContext ctx,
            ILogger log)
        {
            var req = ctx.GetInput<(int numOfMessages, string testRunId, int workTime)>();

            var activities = Enumerable.Empty<Task<bool>>().ToList();
            for (var i = 0; i < req.numOfMessages; i++)
            {
                try
                {
                    activities.Add(ctx.CallActivityAsync<bool>(nameof(PostMessageToEventGrid), (i, req.testRunId, req.workTime)));
                }
                catch (Exception ex)
                {
                    log.LogError(ex, @"An error occurred queuing message generation to Storage Queue");
                    return JObject.FromObject(new { Error = $@"An error occurred executing orchestration {ctx.InstanceId}: {ex}" });
                }
            }

            return (await Task.WhenAll(activities)).All(r => r)    // return 'true' if all are 'true', 'false' otherwise
                    ? JObject.FromObject(new { TestRunId = req.testRunId })
                    : JObject.FromObject(new { Error = $@"An error occurred executing orchestration {ctx.InstanceId}" });

        }

        private const int MAX_RETRY_ATTEMPTS = 10;
        private static readonly Lazy<string> _messageContent = new Lazy<string>(() =>
        {
            using var sr = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream($@"Producer.messagecontent.txt"));
            return sr.ReadToEnd();
        });

        [FunctionName(nameof(PostMessageToEventGrid))]
        public async Task<bool> PostMessageToEventGrid([ActivityTrigger] IDurableActivityContext ctx,
            [EventGrid(TopicEndpointUri = "EventGridTopicEndpoint", TopicKeySetting = "EventGridTopicKey")] IAsyncCollector<EventGridEvent> gridMessages,
            ILogger log)
        {
            var msgDetails = ctx.GetInput<(int id, string runId, int workTime)>();
            var retryCount = 0;
            var retry = false;

            var dataToPost = JObject.FromObject(new
            {
                Content = _messageContent.Value,
                MessageId = msgDetails.id,
                TestRunId = msgDetails.runId
            });

            if (msgDetails.workTime > 0)
            {
                dataToPost.Add(@"workTime", msgDetails.workTime);
            }

            var messageToPost = new EventGridEvent(Guid.NewGuid().ToString(), @"azure-samples/durable-functions-producer-consumer/event", dataToPost, "producerEvent", DateTime.UtcNow, @"1.0");

            do
            {
                retryCount++;
                try
                {
                    await gridMessages.AddAsync(messageToPost);
                    retry = false;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, $@"Error posting message {dataToPost.Value<int>(@"MessageId")}. Retrying...");
                    retry = true;
                }

                if (retry && retryCount >= MAX_RETRY_ATTEMPTS)
                {
                    log.LogError($@"Unable to post message {dataToPost.Value<int>(@"MessageId")} after {retryCount} attempt(s). Giving up.");
                    break;
                }
                else
                {
#if DEBUG
                    log.LogTrace($@"Posted message {dataToPost.Value<int>(@"MessageId")} (Size: {_messageContent.Value.Length} bytes) in {retryCount} attempt(s)");
#else
                log.LogTrace($@"Posted message in {retryCount} attempt(s)");
#endif
                }
            } while (retry);

            return true;
        }
    }
}
