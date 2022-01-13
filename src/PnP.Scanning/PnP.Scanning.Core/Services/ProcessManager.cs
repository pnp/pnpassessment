using Microsoft.Extensions.Logging;
using PnP.Scanning.Core.Executor;
using PnP.Scanning.Core.Orchestrator;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace PnP.Scanning.Core.Services
{
    internal sealed class ProcessManager
    {
        public const int DefaultOrchestratorPort = 25010;
        public const int DefaultExecutorPort = 26010;

        private readonly List<ExecutorProcess> executorProcesses = new();
        private readonly List<OrchestratorProcess> orchestratorProcesses = new();

        private readonly ILogger logger;

        public ProcessManager(ILoggerFactory loggerFactory)
        {
            logger = loggerFactory.CreateLogger<ProcessManager>();
        }

        public int LaunchExecutor(string scope)
        {
            int port = GetFreeExecutorPort();
            int orchestratorPort = GetRunningOrchestrator().Port;

            ProcessStartInfo startInfo = new()
            {
                FileName = "PnP.Scanning.Process.exe",
                Arguments = $"executor {port} {orchestratorPort}",
                UseShellExecute = true
#if !DEBUG
                ,WindowStyle = ProcessWindowStyle.Hidden
#endif
            };

            Process? executorProcess = Process.Start(startInfo);

            if (executorProcess != null && !executorProcess.HasExited)
            {

                executorProcesses.Add(new ExecutorProcess(executorProcess.Id, port, scope));

#if DEBUG
                AttachDebugger(executorProcess);
#endif

                return port;
            }
            else
            {
                return -1;
            }

        }

        public int LaunchOrchestrator(string scope)
        {
            int port = GetFreeOrchestratorPort();

            ProcessStartInfo startInfo = new()
            {
                FileName = "PnP.Scanning.Process.exe",
                Arguments = $"orchestrator {port}",
                UseShellExecute = true
#if !DEBUG
                ,WindowStyle = ProcessWindowStyle.Hidden
#endif
            };

            Process? executorProcess = Process.Start(startInfo);

            if (executorProcess != null && !executorProcess.HasExited)
            {

                orchestratorProcesses.Add(new OrchestratorProcess(executorProcess.Id, port, scope));

#if DEBUG
                AttachDebugger(executorProcess);
#endif

                return port;
            }
            else
            {
                return -1;
            }
        }

        public void RegisterOrchestrator(long processId, int port)
        {
            orchestratorProcesses.Add(new OrchestratorProcess(processId, port, null));
        }

        private int GetFreeExecutorPort()
        {
            if (!executorProcesses.Any())
            {
                return DefaultExecutorPort;
            }
            else
            {
                return GetAvailablePort(DefaultExecutorPort);
            }
        }

        private int GetFreeOrchestratorPort()
        {
            if (!orchestratorProcesses.Any())
            {
                return DefaultOrchestratorPort;
            }
            else
            {
                return GetAvailablePort(DefaultOrchestratorPort);
            }
        }

        private OrchestratorProcess GetRunningOrchestrator()
        {
            return orchestratorProcesses.First();
        }

        /// <summary>
        /// checks for used ports and retrieves the first free port
        /// </summary>
        /// <returns>the free port or 0 if it did not find a free port</returns>
        public static int GetAvailablePort(int startingPort)
        {
            IPEndPoint[] endPoints;
            List<int> portArray = new();

            IPGlobalProperties properties = IPGlobalProperties.GetIPGlobalProperties();

            //getting active connections
            TcpConnectionInformation[] connections = properties.GetActiveTcpConnections();
            portArray.AddRange(from n in connections
                               where n.LocalEndPoint.Port >= startingPort
                               select n.LocalEndPoint.Port);

            //getting active tcp listners
            endPoints = properties.GetActiveTcpListeners();
            portArray.AddRange(from n in endPoints
                               where n.Port >= startingPort
                               select n.Port);

            //getting active udp listeners
            endPoints = properties.GetActiveUdpListeners();
            portArray.AddRange(from n in endPoints
                               where n.Port >= startingPort
                               select n.Port);

            portArray.Sort();

            for (int i = startingPort; i < ushort.MaxValue; i++)
            {
                if (!portArray.Contains(i))
                {
                    return i;
                }
            }

            return 0;
        }

#if DEBUG
        internal static void AttachDebugger(Process processToAttachTo)
        {
            var vsProcess = VisualStudioAttacher.GetVisualStudioForSolutions(new List<string> { "PnP.Scanning.sln" });

            if (vsProcess != null)
            {
                VisualStudioAttacher.AttachVisualStudioToProcess(vsProcess, processToAttachTo /*Process.GetCurrentProcess()*/);
            }
            else
            {
                // try and attach the old fashioned way
                Debugger.Launch();
            }

            // ReSharper disable once ConditionIsAlwaysTrueOrFalse
            if (Debugger.IsAttached)
            {

            }
        }
#endif
    }
}
