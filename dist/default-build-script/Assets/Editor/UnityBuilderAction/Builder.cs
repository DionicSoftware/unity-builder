#nullable disable
using System;
using System.Linq;
using System.Reflection;
using UnityBuilderAction.Input;
using UnityBuilderAction.Reporting;
using UnityBuilderAction.Versioning;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityBuilderAction
{
  static class Builder
  {

    private static bool requestShutdown; 
    private static void LogMessageReceivedThreaded(string message, string stacktrace, LogType logType) {
        if (!(logType == LogType.Error || logType == LogType.Exception || logType == LogType.Assert)) { return; }

        // ingore some Unity errors that pop up with -nographics command line option
        if (message.StartsWith("Video shaders not found.")) { return; }
        if (message.Contains("has an unsupported or invalid shader. Texture will not be rendered.")) { return; }

            
        requestShutdown = true;
    }

    
    public static void BuildProject()
    {
      Debug.Log("Reseting Script Meta Files before building...");
      UnityUtil.HardResetScriptMetaFiles();
      Debug.Log("Script Meta Files reset done, building...");

      requestShutdown = false;
      Application.logMessageReceivedThreaded += LogMessageReceivedThreaded;

      DionicJobModule dionicJobModule = new DionicJobModule();
      ValidationModule validationModule = new ValidationModule(dionicJobModule);
      validationModule.ValidateAssets(true);

      Application.logMessageReceivedThreaded -= LogMessageReceivedThreaded;
      if (requestShutdown) {
          UnityEditor.EditorApplication.Exit(1);
      }
      
      // Gather values from args
      var options = ArgumentsParser.GetValidatedOptions();

      // Gather values from project
      var scenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).Select(s => s.path).ToArray();
      
      // Get all buildOptions from options
      BuildOptions buildOptions = BuildOptions.None;
      foreach (string buildOptionString in Enum.GetNames(typeof(BuildOptions))) {
        if (options.ContainsKey(buildOptionString)) {
          BuildOptions buildOptionEnum = (BuildOptions) Enum.Parse(typeof(BuildOptions), buildOptionString);
          buildOptions |= buildOptionEnum;
        }
      }

#if UNITY_2021_2_OR_NEWER
      // Determine subtarget
      StandaloneBuildSubtarget buildSubtarget;
      if (!options.TryGetValue("standaloneBuildSubtarget", out var subtargetValue) || !Enum.TryParse(subtargetValue, out buildSubtarget)) {
        buildSubtarget = default;
      }
#endif

      // Define BuildPlayer Options
      var buildPlayerOptions = new BuildPlayerOptions {
        scenes = scenes,
        locationPathName = options["customBuildPath"],
        target = (BuildTarget) Enum.Parse(typeof(BuildTarget), options["buildTarget"]),
        options = buildOptions,
#if UNITY_2021_2_OR_NEWER
        subtarget = (int) buildSubtarget
#endif
      };

      // Set version for this build
      VersionApplicator.SetVersion(options["buildVersion"]);
      
      // Apply Android settings
      if (buildPlayerOptions.target == BuildTarget.Android)
      {
        VersionApplicator.SetAndroidVersionCode(options["androidVersionCode"]);
        AndroidSettings.Apply(options);
      }
      
      // Execute default AddressableAsset content build, if the package is installed.
      // Version defines would be the best solution here, but Unity 2018 doesn't support that,
      // so we fall back to using reflection instead.
      var addressableAssetSettingsType = Type.GetType(
        "UnityEditor.AddressableAssets.Settings.AddressableAssetSettings,Unity.Addressables.Editor");
      if (addressableAssetSettingsType != null)
      {
        // ReSharper disable once PossibleNullReferenceException, used from try-catch
        try
        {
          addressableAssetSettingsType.GetMethod("CleanPlayerContent", BindingFlags.Static | BindingFlags.Public)
                .Invoke(null, new object[] {null});
          addressableAssetSettingsType.GetMethod("BuildPlayerContent", new Type[0]).Invoke(null, new object[0]);
        }
        catch (Exception e)
        {
          Debug.LogError("Failed to run default addressables build:\n" + e);
        }
      }

      // Perform build
      BuildReport buildReport = BuildPipeline.BuildPlayer(buildPlayerOptions);

      // Summary
      BuildSummary summary = buildReport.summary;
      StdOutReporter.ReportSummary(summary);

      // Result
      BuildResult result = summary.result;
      StdOutReporter.ExitWithResult(result);
    }
  }
}
