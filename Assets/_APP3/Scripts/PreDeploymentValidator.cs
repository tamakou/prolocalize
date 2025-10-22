using UnityEngine;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.LocalizationMaps;

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.XR.OpenXR;
using UnityEngine.XR.Management;
using UnityEditor.XR.Management;
#endif

/// <summary>
/// Pre-deployment validation script to ensure all components are properly configured
/// before deploying to Magic Leap 2 device
/// </summary>
public class PreDeploymentValidator : MonoBehaviour
{
    [Header("Validation Results")]
    [SerializeField] private bool allValidationsPassed = false;
    
    private void Start()
    {
        // Run validation on start (useful for runtime checks)
        ValidateConfiguration();
    }
    
    /// <summary>
    /// Validates the current project configuration for Magic Leap 2 deployment
    /// </summary>
    public void ValidateConfiguration()
    {
        Debug.Log("=== Pre-Deployment Validation Started ===");
        
        bool allPassed = true;
        
        // Check OpenXR Feature
        allPassed &= ValidateOpenXRFeature();
        
        // Check Scene Configuration
        allPassed &= ValidateSceneConfiguration();
        
        // Check Build Settings
        allPassed &= ValidateBuildSettings();
        
        // Check Script References
        allPassed &= ValidateScriptReferences();
        
        allValidationsPassed = allPassed;
        
        if (allPassed)
        {
            Debug.Log("✅ All validations passed! Ready for Magic Leap 2 deployment.");
        }
        else
        {
            Debug.LogError("❌ Some validations failed. Please fix the issues before deployment.");
        }
        
        Debug.Log("=== Pre-Deployment Validation Completed ===");
    }
    
    private bool ValidateOpenXRFeature()
    {
        Debug.Log("Validating OpenXR Localization Maps Feature...");
        
        try
        {
            var feature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();
            if (feature != null && feature.enabled)
            {
                Debug.Log("✅ MagicLeapLocalizationMapFeature is enabled");
                return true;
            }
            else
            {
                Debug.LogError("❌ MagicLeapLocalizationMapFeature is not enabled in OpenXR settings");
                return false;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"❌ Error checking OpenXR feature: {ex.Message}");
            return false;
        }
    }
    
    private bool ValidateSceneConfiguration()
    {
        Debug.Log("Validating Scene Configuration...");
        
        bool sceneValid = true;
        
        // Check for SpaceTestManager
        var spaceTestManager = FindFirstObjectByType<SpaceTestManager>();
        if (spaceTestManager != null)
        {
            Debug.Log("✅ SpaceTestManager found in scene");
        }
        else
        {
            Debug.LogError("❌ SpaceTestManager not found in scene");
            sceneValid = false;
        }
        
        // Check for Canvas
        var canvas = FindFirstObjectByType<Canvas>();
        if (canvas != null)
        {
            Debug.Log("✅ Canvas found in scene");
        }
        else
        {
            Debug.LogError("❌ Canvas not found in scene");
            sceneValid = false;
        }
        
        // Check for UI buttons
        var buttons = FindObjectsByType<UnityEngine.UI.Button>(FindObjectsSortMode.None);
        if (buttons.Length >= 3)
        {
            Debug.Log($"✅ Found {buttons.Length} buttons in scene (expected at least 3)");
        }
        else
        {
            Debug.LogError($"❌ Found only {buttons.Length} buttons in scene (expected at least 3)");
            sceneValid = false;
        }
        
        return sceneValid;
    }
    
    private bool ValidateBuildSettings()
    {
        Debug.Log("Validating Build Settings...");
        
        bool buildValid = true;
        
#if UNITY_EDITOR
        // Check target platform
        if (EditorUserBuildSettings.activeBuildTarget == BuildTarget.Android)
        {
            Debug.Log("✅ Build target is set to Android");
        }
        else
        {
            Debug.LogError("❌ Build target is not set to Android");
            buildValid = false;
        }
        
        // Check XR settings
        var xrGeneralSettings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
        if (xrGeneralSettings != null && xrGeneralSettings.Manager != null && xrGeneralSettings.Manager.activeLoaders.Count > 0)
        {
            Debug.Log("✅ XR Management is configured");
        }
        else
        {
            Debug.LogError("❌ XR Management is not properly configured");
            buildValid = false;
        }
#endif
        
        return buildValid;
    }
    
    private bool ValidateScriptReferences()
    {
        Debug.Log("Validating Script References...");
        
        bool scriptsValid = true;
        
        // Check if SpaceTestManager script exists
        var spaceTestManager = FindFirstObjectByType<SpaceTestManager>();
        if (spaceTestManager != null)
        {
            Debug.Log("✅ SpaceTestManager script is attached and accessible");
        }
        else
        {
            Debug.LogError("❌ SpaceTestManager script not found or not attached");
            scriptsValid = false;
        }
        
        return scriptsValid;
    }
    
    /// <summary>
    /// Manual validation trigger for editor use
    /// </summary>
    [ContextMenu("Run Validation")]
    public void RunValidationManually()
    {
        ValidateConfiguration();
    }
}

#if UNITY_EDITOR
/// <summary>
/// Custom editor for PreDeploymentValidator to add validation button in inspector
/// </summary>
[CustomEditor(typeof(PreDeploymentValidator))]
public class PreDeploymentValidatorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        
        EditorGUILayout.Space();
        
        PreDeploymentValidator validator = (PreDeploymentValidator)target;
        
        if (GUILayout.Button("Run Pre-Deployment Validation", GUILayout.Height(30)))
        {
            validator.ValidateConfiguration();
        }
        
        EditorGUILayout.Space();
        
        EditorGUILayout.HelpBox(
            "This validator checks if the project is properly configured for Magic Leap 2 deployment. " +
            "Run this validation before building and deploying to the device.",
            MessageType.Info
        );
    }
}
#endif