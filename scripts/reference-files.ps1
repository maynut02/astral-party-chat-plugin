function Get-AstralReferencePaths {
    # Relative paths of the local BepInEx/Unity build dependencies.
    @(
        'core/0Harmony.dll'
        'core/BepInEx.Core.dll'
        'core/BepInEx.Unity.IL2CPP.dll'
        'core/Il2CppInterop.Runtime.dll'
        'interop/Il2Cppmscorlib.dll'
        'interop/UnityEngine.CoreModule.dll'
        'interop/UnityEngine.TextRenderingModule.dll'
        'interop/UnityEngine.InputLegacyModule.dll'
        'interop/UnityEngine.IMGUIModule.dll'
        'interop/UnityEngine.ImageConversionModule.dll'
        'interop/UnityEngine.UIModule.dll'
        'interop/UnityEngine.UI.dll'
    )
}
