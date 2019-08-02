## Original Repository

This repository is a fork of [durable-functions-producer-consumer](https://github.com/Azure-Samples/durable-functions-producer-consumer) Azure Sample.

## Overview

A few changes have been made to the Storage Queue producer code in the [Functions.cs](https://github.com/lucashuet93/durable-functions-producer-consumer/blob/master/Producer/StorageQueues/Functions.cs) file. 

1) The Storage Queue producer's orchestrator endpoint now accepts a ```MessageContent``` property in addition to the existing ```NumberOfMessages``` property, rather than using hardcoded message content's that pull from the messagecontent.txt file. The change enables different messages to be sent to the orchestrator without the need to redeploy the function app. The orchestration endpoint request body has the following stucture:

```
{
  "NumberOfMessages": 3,
  "MessageContent": "test message"
}
```

2) The producer attaches a CorrelationId property to each message in a given batch for load testing purposes.

3) The producer now implements a claims-check pattern. When the function receives a request, it writes the ```MessageContent``` to a new file on an FTP server, then sends a new message to the Azure Queue with the file location as the content of the message. Downstream services can now read messages from the Azure Queue more rapidly and pull the actual contents of the message from the FTP server. In order to achieve this, a few values have been added to the local.settings.json file, and will need to be added to the function app's configuration settings once deployed:

```
  "FtpServerBaseUrl": "ftp://000.00.00.00/",
  "FtpServerFolderName": "folder",
  "FtpUsername": "username",
  "FtpPassword": "password"
```