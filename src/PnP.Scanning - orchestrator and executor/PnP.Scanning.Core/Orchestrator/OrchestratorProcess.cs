namespace PnP.Scanning.Core.Orchestrator
{
    internal sealed class OrchestratorProcess
    {
        internal OrchestratorProcess(long processId, int port, string scope)
        {
            ProcessId = processId;
            Port = port;
            Scope = scope;
        }

        internal long ProcessId { get; private set; }

        internal int Port { get; private set; }

        internal string Scope { get; private set; }
    }
}
