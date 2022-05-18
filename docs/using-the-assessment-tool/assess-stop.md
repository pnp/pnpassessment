# Stop the assessment tool

Once you've used the assessment tool there will be a background process (`microsoft365-assessment.exe`) that stays running. This is by design as the Microsoft 365 Assessment tool actually is a web server that runs on your computer. When you use the CLI to instruct the Microsoft 365 Assessment tool you're launching also `microsoft365-assessment.exe`, but this time in CLI mode. This CLI process will use gRPC to communicate with the running background process on localhost, port 25010.

The background `microsoft365-assessment.exe` process can be stopped using various means like your OS task manager or command line, but there's also a `stop` command that you can use.

## Sample stop commands

Task | CLI
-----|------
Stop the running background Microsoft 365 Assessment tool process | microsoft365-assessment.exe stop
