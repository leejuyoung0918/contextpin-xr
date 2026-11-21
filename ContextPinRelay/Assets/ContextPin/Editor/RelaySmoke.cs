using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;

namespace ContextPin.Editor
{
    [InitializeOnLoad]
    public static class RelaySmoke
    {
        static RelaySmoke()
        {
            EditorApplication.playModeStateChanged+=state=>
            {
                if(state==PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("RelaySmoke",false))
                {
                    SessionState.SetBool("RelaySmoke",false);
                    RelaySmokeDriver.Begin();
                }
                if(state==PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("TraceSimulatorSmoke",false))
                {
                    SessionState.SetBool("TraceSimulatorSmoke",false);
                    TraceSimulatorSmoke.Begin();
                }
                if(state==PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("MeetingSmoke",false))
                {
                    SessionState.SetBool("MeetingSmoke",false);
                    MeetingSmoke.Begin(SessionState.GetBool("ContextPinExplicitSimulator",false));
                }
                if(state==PlayModeStateChange.EnteredEditMode && SessionState.GetBool("TraceSimulatorExit",false))
                {
                    SessionState.SetBool("TraceSimulatorExit",false);
                    var code=SessionState.GetInt("TraceSimulatorExitCode",2);
                    EditorApplication.delayCall+=()=>EditorApplication.Exit(code);
                }
            };
        }
        public static void Run()
        {
            var root=Environment.GetEnvironmentVariable("RELAY_WORKSPACE")??Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var data=Path.Combine(root,"artifacts","smoke-data-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            Environment.SetEnvironmentVariable("RELAY_TEST_DATA",data);
            RelaySetup.Configure();
            SessionState.SetBool("RelaySmoke",true);
            RelaySetup.DesktopPreview();
        }

        public static void RunSimulator()
        {
            SetupSimulator("TraceSimulatorSmoke");
        }

        public static void RunMeeting()
        {
            SessionState.SetBool("MeetingSmoke",true);
            RelaySetup.DesktopPreview();
        }

        public static void RunMeetingSimulator()
        {
            SetupSimulator("MeetingSmoke");
        }

        static void SetupSimulator(string checkName)
        {
            string root=Environment.GetEnvironmentVariable("RELAY_WORKSPACE");
            string runtime=Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
            if(string.IsNullOrEmpty(root) || string.IsNullOrEmpty(runtime) || !File.Exists(runtime))
                throw new InvalidOperationException("Start the simulator check with scripts/relay.py smoke-simulator");
            var data=Path.Combine(root,"artifacts","simulator-data-"+DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            Environment.SetEnvironmentVariable("RELAY_TEST_DATA",data);
            const string scene="Assets/ContextPin/Scenes/Relay.unity";
            if(!File.Exists(scene))RelaySetup.Configure();
            EditorSceneManager.OpenScene(scene);
            var settings=XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            if(settings==null)throw new InvalidOperationException("Missing Standalone XR settings");
            settings.InitManagerOnStart=true;
            SessionState.SetBool("ContextPinExplicitSimulator",true);
            SessionState.SetBool(checkName,true);
            EditorApplication.ExecuteMenuItem("Window/General/Game");
            EditorApplication.isPlaying=true;
        }
    }
}
