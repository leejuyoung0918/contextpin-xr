using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;

namespace ContextPin.Editor
{
    [InitializeOnLoad]
    public static class RelaySetup
    {
        const string ScenePath="Assets/ContextPin/Scenes/Relay.unity";
        static RelaySetup()
        {
            if(!Application.isBatchMode)EditorApplication.delayCall+=()=>
            {
                if(!File.Exists(ScenePath)&&!EditorApplication.isPlayingOrWillChangePlaymode)Configure();
            };
        }

        [MenuItem("ContextPin/1. Prepare Project")]
        public static void Configure()
        {
            Directory.CreateDirectory("Assets/ContextPin/Scenes");
            Directory.CreateDirectory("Assets/ContextPin/Generated");
            AssetDatabase.Refresh();
            PlayerSettings.companyName="ContextPin";
            PlayerSettings.productName="ContextPin XR Phone Stand";
            PlayerSettings.bundleVersion="0.3.0";
            PlayerSettings.Android.bundleVersionCode=3;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android,"org.contextpin.relay.phonestand");
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android,ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures=AndroidArchitecture.ARM64;
            PlayerSettings.Android.minSdkVersion=AndroidSdkVersions.AndroidApiLevel32;
            PlayerSettings.Android.targetSdkVersion=AndroidSdkVersions.AndroidApiLevelAuto;
            PlayerSettings.Android.forceInternetPermission=true;
            PlayerSettings.Android.applicationEntry=AndroidApplicationEntry.Activity;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android,new[]{GraphicsDeviceType.Vulkan});
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneOSX,false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneOSX,new[]{GraphicsDeviceType.Metal});
            PlayerSettings.colorSpace=ColorSpace.Linear;
            PlayerSettings.runInBackground=true;
            PlayerSettings.allowUnsafeCode=false;
            PlayerSettings.insecureHttpOption=InsecureHttpOption.DevelopmentOnly;
            PlayerSettings.macOS.microphoneUsageDescription="회의 발화를 기록하고 합의한 설계 변경을 요청합니다.";
            var playerSettings=new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            var input=playerSettings.FindProperty("activeInputHandler");
            if(input!=null){input.intValue=1;playerSettings.ApplyModifiedPropertiesWithoutUndo();}

            ConfigureXR();
            var meta=OVRProjectConfig.CachedProjectConfig;
            meta.targetDeviceTypes=new System.Collections.Generic.List<OVRProjectConfig.DeviceType>{OVRProjectConfig.DeviceType.Quest3,OVRProjectConfig.DeviceType.Quest3S};
            meta.handTrackingSupport=OVRProjectConfig.HandTrackingSupport.ControllersAndHands;
            meta.insightPassthroughSupport=OVRProjectConfig.FeatureSupport.Supported;
            meta.anchorSupport=OVRProjectConfig.AnchorSupport.Enabled;
            meta.sceneSupport=OVRProjectConfig.FeatureSupport.Required;
            meta.isPassthroughCameraAccessEnabled=true;
            meta.enableNSCConfig=true;
            meta.securityXmlPath="Assets/ContextPin/Editor/network_sec_config.xml";
            OVRProjectConfig.CommitProjectConfig(meta);
            EnsureRig();
            if(!File.Exists(ScenePath))CreateScene();
            EditorBuildSettings.scenes=new[]{new EditorBuildSettingsScene(ScenePath,true)};
            AssetDatabase.SaveAssets();
            Debug.Log("ContextPin setup complete. Open Relay.unity, then choose Desktop or Meta XR Simulator preview.");
        }

        static void ConfigureXR()
        {
            if(!EditorBuildSettings.TryGetConfigObject<XRGeneralSettingsPerBuildTarget>(XRGeneralSettings.k_SettingsKey,out var settings))
            {
                settings=ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(settings,"Assets/ContextPin/Generated/XRSettings.asset");
                EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey,settings,true);
            }
            foreach(var group in new[]{BuildTargetGroup.Standalone,BuildTargetGroup.Android})
            {
                if(!settings.HasSettingsForBuildTarget(group))settings.CreateDefaultSettingsForBuildTarget(group);
                if(!settings.HasManagerSettingsForBuildTarget(group))settings.CreateDefaultManagerSettingsForBuildTarget(group);
                var general=settings.SettingsForBuildTarget(group);
                // Device builds always start XR. Desktop startup is an explicit menu choice.
                general.InitManagerOnStart=group==BuildTargetGroup.Android;
                if(!XRPackageMetadataStore.AssignLoader(general.Manager,"UnityEngine.XR.OpenXR.OpenXRLoader",group))
                    throw new InvalidOperationException("Could not assign OpenXR loader for "+group);
                FeatureHelpers.RefreshFeatures(group);
                var xr=OpenXRSettings.GetSettingsForBuildTargetGroup(group);
                if(xr==null)throw new InvalidOperationException("OpenXR settings unavailable for "+group);
                var enabled=new[]{"MetaXRFeature","OculusTouchControllerProfile","MetaQuestTouchPlusControllerProfile","HandTracking","MetaHandTrackingAim"};
                foreach(var f in xr.GetFeatures<OpenXRFeature>())if(enabled.Contains(f.GetType().Name))f.enabled=true;
                xr.renderMode=OpenXRSettings.RenderMode.SinglePassInstanced;
                EditorUtility.SetDirty(general);EditorUtility.SetDirty(general.Manager);EditorUtility.SetDirty(xr);
            }
            EditorUtility.SetDirty(settings);
        }
        static void EnsureRig()
        {
            const string path="Assets/ContextPin/Generated/RelayRig.prefab";
            if(File.Exists(path))return;
            var source=AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.meta.xr.sdk.core/Prefabs/OVRCameraRig.prefab");
            if(source==null)throw new InvalidOperationException("Meta XR Core OVRCameraRig prefab is missing");
            var rig=(GameObject)PrefabUtility.InstantiatePrefab(source);
            var cameraRig=rig.GetComponent<OVRCameraRig>();
            AddHand(cameraRig.leftHandAnchor,OVRHand.Hand.HandLeft);
            AddHand(cameraRig.rightHandAnchor,OVRHand.Hand.HandRight);
            PrefabUtility.SaveAsPrefabAsset(rig,path);UnityEngine.Object.DestroyImmediate(rig);
        }
        static void AddHand(Transform anchor,OVRHand.Hand type)
        {
            var obj=new GameObject(type.ToString());obj.transform.SetParent(anchor,false);
            var hand=obj.AddComponent<OVRHand>();var serialized=new SerializedObject(hand);
            serialized.FindProperty("HandType").intValue=(int)type;serialized.ApplyModifiedPropertiesWithoutUndo();
        }
        static void CreateScene()
        {
            var scene=EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
            var app=new GameObject("ContextPin Relay").AddComponent<RelayApp>();
            app.xrRigPrefab=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/ContextPin/Generated/RelayRig.prefab");
            app.koreanFont=AssetDatabase.LoadAssetAtPath<Font>("Assets/ContextPin/Fonts/NanumGothic-Regular.ttf");
            RenderSettings.ambientLight=new Color(.6f,.7f,.8f);
            EditorSceneManager.SaveScene(scene,ScenePath);
        }

        [MenuItem("ContextPin/2. Desktop Preview (no headset)")]
        public static void DesktopPreview()
        {
            if(EditorApplication.isPlaying)return;
            SessionState.SetBool("ContextPinExplicitSimulator",false);
            EnsureScene();XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone).InitManagerOnStart=false;
            EditorApplication.isPlaying=true;
        }
        [MenuItem("ContextPin/3. Meta XR Simulator Preview")]
        public static void SimulatorPreview()
        {
            if(EditorApplication.isPlaying)return;
            EnsureScene();XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone).InitManagerOnStart=true;
            SessionState.SetBool("ContextPinExplicitSimulator",true);
            if(!(Environment.GetEnvironmentVariable("XR_RUNTIME_JSON")??"").EndsWith("meta_openxr_simulator.json",StringComparison.OrdinalIgnoreCase))
                EditorApplication.ExecuteMenuItem("Window/Meta/Meta XR Simulator/Activate");
            EditorApplication.isPlaying=true;
        }
        static void EnsureScene()
        {
            if(!File.Exists(ScenePath))Configure();
            if(EditorSceneManager.GetActiveScene().path!=ScenePath)
            {
                if(!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())throw new OperationCanceledException();
                EditorSceneManager.OpenScene(ScenePath);
            }
        }
        [MenuItem("ContextPin/4. Build Quest APK")]
        public static void BuildQuest()
        {
            Configure();
            var root=Environment.GetEnvironmentVariable("RELAY_WORKSPACE")??Path.GetFullPath(Path.Combine(Application.dataPath,"../.."));
            var output=Path.Combine(root,"builds/ContextPinPhoneStand.apk");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var report=BuildPipeline.BuildPlayer(new BuildPlayerOptions{scenes=new[]{ScenePath},locationPathName=output,target=BuildTarget.Android,
                options=BuildOptions.Development});
            if(report.summary.result!=BuildResult.Succeeded)throw new BuildFailedException("Quest build failed: "+report.summary.result);
            Debug.Log("Quest APK ready: "+output);
        }
    }
}
