using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace NfsMw.EditorTools
{
    public static class NFSMWBuild
    {
        const string Output = "Build/NFSMWUnity/NFSMWUnity.exe";

        /// <summary>Builds the Windows player; gameroot.txt next to it points at the installed game data.</summary>
        [MenuItem("NFSMW/Build Windows Player")]
        public static void BuildWindows()
        {
            if (!File.Exists("Assets/Scenes/FreeRoam.unity")) NFSMWSetup.CreateScene();
            NFSMWSetup.IncludeShaders();
            PlayerSettings.productName = "NFS Most Wanted (Unity)";
            PlayerSettings.companyName = "NFSMW Rebuild";
            PlayerSettings.defaultIsNativeResolution = true;
            PlayerSettings.fullScreenMode = FullScreenMode.FullScreenWindow;
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/FreeRoam.unity" },
                locationPathName = Output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });
            if (report.summary.result != BuildResult.Succeeded)
            {
                Debug.LogError($"NFSMW build: {report.summary.result}, {report.summary.totalErrors} errors");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(Output), "gameroot.txt"), GameData.ResolveRoot());
            Debug.Log($"NFSMW build: {Output} ({report.summary.totalSize / (1024 * 1024)} MB)");
        }
    }
}
