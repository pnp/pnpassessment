#if DEBUG

using EnvDTE;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DTEProcess = EnvDTE.Process;

namespace PnP.Scanning.Process.Services
{

    /// <summary>
    /// Example taken from <a href="https://gist.github.com/3813175">this gist</a>.
    /// </summary>
    internal static class VisualStudioManager
    {
        [DllImport("ole32.dll")]
        public static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);

        [DllImport("ole32.dll")]
        public static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

        /// <summary>
        /// The method to use to attach visual studio to a specified process.
        /// </summary>
        /// <param name="visualStudioProcess">
        /// The visual studio process to attach to.
        /// </param>
        /// <param name="applicationProcess">
        /// The application process that needs to be debugged.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the application process is null.
        /// </exception>
        public static void AttachVisualStudioToProcess(System.Diagnostics.Process visualStudioProcess, System.Diagnostics.Process applicationProcess)
        {
            _DTE visualStudioInstance;

            if (TryGetVsInstance(visualStudioProcess.Id, out visualStudioInstance))
            {
                if (visualStudioInstance != null)
                {
                    // Find the process you want the VS instance to attach to...
                    DTEProcess processToAttachTo =
                        visualStudioInstance.Debugger.LocalProcesses.Cast<DTEProcess>()
                                            .FirstOrDefault(process => process.ProcessID == applicationProcess.Id);

                    // Attach to the process.
                    if (processToAttachTo != null)
                    {
                        processToAttachTo.Attach();

                        //ShowWindow((int)visualStudioProcess.MainWindowHandle, 3);
                        //SetForegroundWindow(visualStudioProcess.MainWindowHandle);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "Visual Studio process cannot find specified application '" + applicationProcess.Id + "'");
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                            "Visual Studio process cannot find specified application '" + applicationProcess.Id + "'");
                }
            }
        }

        /// <summary>
        /// The get visual studio for solutions.
        /// </summary>
        /// <param name="solutionNames">
        /// The solution names.
        /// </param>
        /// <returns>
        /// The <see cref="Process"/>.
        /// </returns>
        public static System.Diagnostics.Process GetVisualStudioForSolutions(List<string> solutionNames)
        {
            foreach (string solution in solutionNames)
            {
                System.Diagnostics.Process visualStudioForSolution = GetVisualStudioForSolution(solution);
                if (visualStudioForSolution != null)
                {
                    return visualStudioForSolution;
                }
            }

            return null;
        }

        /// <summary>
        /// The get visual studio process that is running and has the specified solution loaded.
        /// </summary>
        /// <param name="solutionName">
        /// The solution name to look for.
        /// </param>
        /// <returns>
        /// The visual studio <see cref="Process"/> with the specified solution name.
        /// </returns>
        public static System.Diagnostics.Process GetVisualStudioForSolution(string solutionName)
        {
            IEnumerable<System.Diagnostics.Process> visualStudios = GetVisualStudioProcesses();

            foreach (System.Diagnostics.Process visualStudio in visualStudios)
            {
                if (TryGetVsInstance(visualStudio.Id, out _DTE visualStudioInstance))
                {
                    try
                    {
                        if (visualStudioInstance != null)
                        {
                            string actualSolutionName = Path.GetFileName(visualStudioInstance.Solution.FullName);

                            if (string.Compare(
                                actualSolutionName,
                                solutionName,
                                StringComparison.InvariantCultureIgnoreCase) == 0)
                            {
                                return visualStudio;
                            }
                        }
                    }
                    catch (Exception)
                    {
                        throw;
                    }
                }
            }

            return null;
        }

        private static IEnumerable<System.Diagnostics.Process> GetVisualStudioProcesses()
        {
            System.Diagnostics.Process[] processes = System.Diagnostics.Process.GetProcesses();
            return processes.Where(o => o.ProcessName.Contains("devenv"));
        }

        private static bool TryGetVsInstance(int processId, out _DTE instance)
        {
            IntPtr numFetched = IntPtr.Zero;
            IMoniker[] monikers = new IMoniker[1];

            _ = GetRunningObjectTable(0, out IRunningObjectTable runningObjectTable);
            runningObjectTable.EnumRunning(out IEnumMoniker monikerEnumerator);
            monikerEnumerator.Reset();

            while (monikerEnumerator.Next(1, monikers, numFetched) == 0)
            {
                _ = CreateBindCtx(0, out IBindCtx ctx);

                monikers[0].GetDisplayName(ctx, null, out string runningObjectName);

                runningObjectTable.GetObject(monikers[0], out object runningObjectVal);

                if (runningObjectVal is _DTE dTE && runningObjectName.StartsWith("!VisualStudio"))
                {
                    int currentProcessId = int.Parse(runningObjectName.Split(':')[1]);

                    if (currentProcessId == processId)
                    {
                        instance = dTE;
                        return true;
                    }
                }
            }

            instance = null;
            return false;
        }
    }
}

#endif
