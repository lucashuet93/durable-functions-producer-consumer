using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Producer.StorageQueues
{
    public static class Functions
    {
        [FunctionName(nameof(PostToStorageQueue))]
        public static async Task<HttpResponseMessage> PostToStorageQueue(
            [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestMessage request,
            [OrchestrationClient]DurableOrchestrationClient client,
            ILogger log)
        {
            var inputObject = JObject.Parse(await request.Content.ReadAsStringAsync());
            var numberOfMessages = inputObject.Value<int>(@"NumberOfMessages");
            var messageContent = inputObject.Value<string>(@"MessageContent");

            var workTime = -1;
            if (inputObject.TryGetValue(@"WorkTime", out var workTimeVal))
            {
                workTime = workTimeVal.Value<int>();
            }

            var testRunId = Guid.NewGuid().ToString();
            var orchId = await client.StartNewAsync(nameof(GenerateMessagesForStorageQueue),
                    (numberOfMessages, messageContent, testRunId, workTime));

            log.LogTrace($@"Kicked off {numberOfMessages} message creation...");

            return await client.WaitForCompletionOrCreateCheckStatusResponseAsync(request, orchId, TimeSpan.FromMinutes(2));
        }

        [FunctionName(nameof(GenerateMessagesForStorageQueue))]
        public static async Task<JObject> GenerateMessagesForStorageQueue(
            [OrchestrationTrigger]DurableOrchestrationContext ctx,
            ILogger log)
        {
            var req = ctx.GetInput<(int numOfMessages, string messageContent, string testRunId, int workTime)>();
            string batchCorrelationId = CreateCorrelationId();
            var activities = Enumerable.Empty<Task<bool>>().ToList();
            for (var i = 0; i < req.numOfMessages; i++)
            {
                try
                {
                    activities.Add(ctx.CallActivityAsync<bool>(nameof(PostMessageToStorageQueue), (i, req.messageContent, batchCorrelationId, req.testRunId, req.workTime)));
                }
                catch (Exception ex)
                {
                    log.LogError(ex, @"An error occurred queuing message generation to Storage Queue");
                    return JObject.FromObject(new { Error = $@"An error occurred executing orchestration {ctx.InstanceId}: {ex.ToString()}" });
                }
            }

            return (await Task.WhenAll(activities)).All(r => r)    // return 'true' if all are 'true', 'false' otherwise
                    ? JObject.FromObject(new { TestRunId = req.testRunId })
                    : JObject.FromObject(new { Error = $@"An error occurred executing orchestration {ctx.InstanceId}" });
        }

        private const int MAX_RETRY_ATTEMPTS = 10;

        [FunctionName(nameof(PostMessageToStorageQueue))]
        public static async Task<bool> PostMessageToStorageQueue([ActivityTrigger]DurableActivityContext ctx,
            [Queue("%StorageQueueName%", Connection = @"StorageQueueConnection")]IAsyncCollector<JObject> queueMessages,
            ILogger log)
        {
            var msgDetails = ctx.GetInput<(int id, string messageContent, string batchCorrelationId, string runId, int workTime)>();
            var retryCount = 0;
            var retry = false;

            // create new file on FTP server with message content as file contents
            string ftpFileLocation = CreateFile(msgDetails.messageContent);

            // create queue message with correlationId and reference to newly created FTP file location as the content property
            var messageToPost = JObject.FromObject(new
            {
                Content = ftpFileLocation,
                CorrelationId = msgDetails.batchCorrelationId,
                EnqueueTimeUtc = DateTime.UtcNow,
                MessageId = msgDetails.id,
                TestRunId = msgDetails.runId
            });

            if (msgDetails.workTime > 0)
            {
                messageToPost.Add(@"workTime", msgDetails.workTime);
            }

            do
            {
                retryCount++;
                try
                {
                    await queueMessages.AddAsync(messageToPost);
                    retry = false;
                }
                catch (Exception ex)
                {
                    log.LogError(ex, $@"Error posting message {messageToPost.Value<int>(@"MessageId")}. Retrying...");
                    retry = true;
                }

                if (retry && retryCount >= MAX_RETRY_ATTEMPTS)
                {
                    log.LogError($@"Unable to post message {messageToPost.Value<int>(@"MessageId")} after {retryCount} attempt(s). Giving up.");
                    break;
                }
                else
                {
#if DEBUG
                    log.LogTrace($@"Posted message {messageToPost.Value<int>(@"MessageId")} (Size: {msgDetails.messageContent.Length} bytes) in {retryCount} attempt(s)");
#else
                log.LogTrace($@"Posted message in {retryCount} attempt(s)");
#endif
                }
            } while (retry);

            return true;
        }

        static string CreateCorrelationId()
        {
            // Instantiate random number generator 
            Random rand = new Random();
            // Instantiate an array of byte 
            byte[] byteArray = new Byte[24];
            rand.NextBytes(byteArray);

            string correlationId = Convert.ToBase64String(byteArray);
            Console.WriteLine(correlationId);
            Console.WriteLine(correlationId.Length);
            return correlationId;
        }

        static string CreateFile(string textContent)
        {
            // create a guid for a unique file name
            Guid guid = Guid.NewGuid();
            string ftpFileLocation = $"{Environment.GetEnvironmentVariable("FtpServerBaseUrl")}{Environment.GetEnvironmentVariable("FtpServerFolderName")}/{guid}.txt";

            // get the object used to communicate with the server.
            FtpWebRequest request = (FtpWebRequest)WebRequest.Create(ftpFileLocation);
            request.Method = WebRequestMethods.Ftp.UploadFile;
            request.Credentials = new NetworkCredential(Environment.GetEnvironmentVariable("FtpUsername").Normalize(), Environment.GetEnvironmentVariable("FtpPassword").Normalize());

            // convert contents to byte.
            byte[] fileContents = Encoding.ASCII.GetBytes(textContent);

            request.ContentLength = fileContents.Length;

            using (Stream requestStream = request.GetRequestStream())
            {
                requestStream.Write(fileContents, 0, fileContents.Length);
            }

            using (FtpWebResponse response = (FtpWebResponse)request.GetResponse())
            {
                Console.WriteLine($"Upload File Complete, status {response.StatusDescription}");
            }

            // return the file location so it can be written as the content in the azure queue message
            return ftpFileLocation;
        }
    }
}
