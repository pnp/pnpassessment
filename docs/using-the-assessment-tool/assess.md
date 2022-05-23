# Running an assessment

Once you've [downloaded](download.md) the Microsoft 365 Assessment tool, [configured authentication](setupauth.md) and [setup optional environment configuration](configuration.md) you're now ready to run your an assessment. A typical assessment will contain these steps:

- [Start](assess-start.md) the assessment
- [Stay informed](assess-operations.md) about the assessment status
- Optionally [Pause/restart](assess-operations.md) the running assessment
- [Generate a report](assess-report.md) containing the assessment details
- [Stop](assess-stop.md) the Microsoft 365 Assessment tool

You'll use the Microsoft 365 Assessment tool CLI (command line interface) for above steps, checkout the linked pages for more details on each step.

## Microsoft 365 Assessment tool CLI

To use the Microsoft 365 Assessment tool you'll need to use the command line interface (CLI) and that can be done in two modes.

### Launch without command line arguments

In this mode the Microsoft 365 Assessment tool CLI will be started (e.g., by double clicking the binary) and then allows you to keep providing arguments like `List`, `Start`, `Status` and more.

![argument less gif](../images/assessmentnoarguments.gif)

> [!Note]
> To leave this mode you need to press enter without specifying an argument.

### Launch with command line arguments

Here you specify the needed command and optionally it's arguments as part of the command line when you execute the Microsoft 365 Assessment tool.

![argument less gif](../images/assessmentarguments.gif)

### Understanding the available command line options

The best way to figure out what's possible is by reading the documentation, but a brief summary of the options is also available if you add the `--help` argument. You can do this for the Microsoft 365 Assessment tool (`microsoft365-assessment.exe --help`) or for a specific command (`microsoft365-assessment.exe start --help`).
