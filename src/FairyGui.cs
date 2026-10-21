using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace AstralPartyChatPlugin;

/// <summary>
/// Calls the game's FairyGUI through IL2CPP metadata. FairyGUI is part of the
/// HybridCLR <c>AstralParty.Runtime</c> assembly, which has no generated interop
/// wrappers. Every method is resolved by its exact parameter types, so a changed
/// game signature raises <see cref="MissingMethodException"/> instead of passing
/// arguments of the wrong layout. Call only on Unity's main thread.
/// </summary>
internal static unsafe class FairyGui
{
    public const string Float = "System.Single";
    public const string Bool = "System.Boolean";
    public const string Int = "System.Int32";
    public const string String = "System.String";
    public const string Vector2Type = "UnityEngine.Vector2";
    public const string ColorType = "UnityEngine.Color";
    private const string RuntimeAssembly = "AstralParty.Runtime";

    private static readonly NativeGameData Metadata = new();
    private static readonly Dictionary<(IntPtr Class, string Name, string Signature), IntPtr> Methods = new();
    private static readonly Dictionary<IntPtr, string> ClassNames = new();
    private static readonly Dictionary<string, (IntPtr Pointer, nint Handle)> LookupStrings = new(StringComparer.Ordinal);

    public static IntPtr Class(string namespaze, string name)
    {
        var klass = Metadata.FindClass(RuntimeAssembly, namespaze, name);
        if (klass == IntPtr.Zero)
            throw new TypeLoadException("Game UI type is unavailable: " + namespaze + "." + name);
        return klass;
    }

    public static bool TryClass(string namespaze, string name, out IntPtr klass)
    {
        klass = Metadata.FindClass(RuntimeAssembly, namespaze, name);
        return klass != IntPtr.Zero;
    }

    public static string ClassName(IntPtr instance)
    {
        if (instance == IntPtr.Zero) return string.Empty;
        return ClassNameOfClass(IL2CPP.il2cpp_object_get_class(instance));
    }

    public static IntPtr Method(IntPtr klass, string name, string signature)
    {
        var key = (klass, name, signature);
        if (Methods.TryGetValue(key, out var cached)) return cached;
        var parameters = signature.Length == 0 ? Array.Empty<string>() : signature.Split(',');
        for (var current = klass; current != IntPtr.Zero; current = IL2CPP.il2cpp_class_get_parent(current))
        {
            var iterator = IntPtr.Zero;
            IntPtr method;
            while ((method = IL2CPP.il2cpp_class_get_methods(current, ref iterator)) != IntPtr.Zero)
            {
                if (IL2CPP.il2cpp_method_get_param_count(method) != parameters.Length
                    || !string.Equals(Marshal.PtrToStringAnsi(IL2CPP.il2cpp_method_get_name(method)), name, StringComparison.Ordinal))
                    continue;
                var matches = true;
                for (var index = 0; matches && index < parameters.Length; index++)
                {
                    var typeName = IL2CPP.il2cpp_type_get_name(IL2CPP.il2cpp_method_get_param(method, (uint)index));
                    try { matches = string.Equals(Marshal.PtrToStringAnsi(typeName), parameters[index], StringComparison.Ordinal); }
                    finally { IL2CPP.il2cpp_free(typeName); }
                }
                if (!matches) continue;
                Methods[key] = method;
                return method;
            }
        }
        throw new MissingMethodException(ClassNameOfClass(klass) + "." + name + "(" + signature + ")");
    }

    private static string ClassNameOfClass(IntPtr klass)
    {
        if (ClassNames.TryGetValue(klass, out var cached)) return cached;
        var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass)) ?? string.Empty;
        ClassNames[klass] = name;
        return name;
    }

    private static IntPtr InstanceMethod(IntPtr instance, string name, string signature)
    {
        if (instance == IntPtr.Zero) throw new NullReferenceException("Game UI object is unavailable for " + name + ".");
        return Method(IL2CPP.il2cpp_object_get_class(instance), name, signature);
    }

    private static IntPtr Invoke(IntPtr method, IntPtr instance, void** arguments)
    {
        var exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(method, instance, arguments, ref exception);
        Il2CppException.RaiseExceptionIfNecessary(exception);
        return result;
    }

    public static IntPtr New(IntPtr klass)
    {
        var instance = IL2CPP.il2cpp_object_new(klass);
        Invoke(Method(klass, ".ctor", string.Empty), instance, null);
        return instance;
    }

    public static IntPtr NewWithObject(IntPtr klass, string signature, IntPtr argument)
    {
        var instance = IL2CPP.il2cpp_object_new(klass);
        void** arguments = stackalloc void*[1];
        arguments[0] = (void*)argument;
        Invoke(Method(klass, ".ctor", signature), instance, arguments);
        return instance;
    }

    public static IntPtr NewWithObjects(IntPtr klass, string signature, IntPtr first, IntPtr second)
    {
        var instance = IL2CPP.il2cpp_object_new(klass);
        void** arguments = stackalloc void*[2];
        arguments[0] = (void*)first;
        arguments[1] = (void*)second;
        Invoke(Method(klass, ".ctor", signature), instance, arguments);
        return instance;
    }

    public static IntPtr Call(IntPtr instance, string name) =>
        Invoke(InstanceMethod(instance, name, string.Empty), instance, null);

    public static IntPtr CallStatic(IntPtr klass, string name) =>
        Invoke(Method(klass, name, string.Empty), IntPtr.Zero, null);

    public static IntPtr Call<T1>(IntPtr instance, string name, string signature, T1 first)
        where T1 : unmanaged
    {
        void** arguments = stackalloc void*[1];
        arguments[0] = &first;
        return Invoke(InstanceMethod(instance, name, signature), instance, arguments);
    }

    public static IntPtr Call<T1, T2>(IntPtr instance, string name, string signature, T1 first, T2 second)
        where T1 : unmanaged where T2 : unmanaged
    {
        void** arguments = stackalloc void*[2];
        arguments[0] = &first;
        arguments[1] = &second;
        return Invoke(InstanceMethod(instance, name, signature), instance, arguments);
    }

    public static IntPtr Call<T1, T2, T3>(IntPtr instance, string name, string signature, T1 first, T2 second, T3 third)
        where T1 : unmanaged where T2 : unmanaged where T3 : unmanaged
    {
        void** arguments = stackalloc void*[3];
        arguments[0] = &first;
        arguments[1] = &second;
        arguments[2] = &third;
        return Invoke(InstanceMethod(instance, name, signature), instance, arguments);
    }

    public static IntPtr CallObject(IntPtr instance, string name, string signature, IntPtr argument)
    {
        void** arguments = stackalloc void*[1];
        arguments[0] = (void*)argument;
        return Invoke(InstanceMethod(instance, name, signature), instance, arguments);
    }

    public static IntPtr CallObjects(IntPtr instance, string name, string signature, IntPtr first, IntPtr second)
    {
        void** arguments = stackalloc void*[2];
        arguments[0] = (void*)first;
        arguments[1] = (void*)second;
        return Invoke(InstanceMethod(instance, name, signature), instance, arguments);
    }

    public static IntPtr CallStaticObject(IntPtr klass, string name, string signature, IntPtr argument)
    {
        void** arguments = stackalloc void*[1];
        arguments[0] = (void*)argument;
        return Invoke(Method(klass, name, signature), IntPtr.Zero, arguments);
    }

    public static IntPtr CallStaticObjects(IntPtr klass, string name, string signature, IntPtr first, IntPtr second)
    {
        void** arguments = stackalloc void*[2];
        arguments[0] = (void*)first;
        arguments[1] = (void*)second;
        return Invoke(Method(klass, name, signature), IntPtr.Zero, arguments);
    }

    public static IntPtr CallValueObject<T1>(IntPtr instance, string name, string signature, T1 first, IntPtr second)
        where T1 : unmanaged
    {
        void** arguments = stackalloc void*[2];
        arguments[0] = &first;
        arguments[1] = (void*)second;
        return Invoke(InstanceMethod(instance, name, signature), instance, arguments);
    }

    public static void DrawRoundRect(IntPtr shape, float lineSize, Color lineColor, Color fillColor, float radius)
    {
        void** arguments = stackalloc void*[7];
        arguments[0] = &lineSize;
        arguments[1] = &lineColor;
        arguments[2] = &fillColor;
        arguments[3] = &radius;
        arguments[4] = &radius;
        arguments[5] = &radius;
        arguments[6] = &radius;
        Invoke(InstanceMethod(shape, "DrawRoundRect",
            Float + "," + ColorType + "," + ColorType + "," + Float + "," + Float + "," + Float + "," + Float), shape, arguments);
    }

    public static T Get<T>(IntPtr instance, string getter) where T : unmanaged
    {
        var boxed = Call(instance, getter);
        if (boxed == IntPtr.Zero) throw new InvalidOperationException("Game UI value is unavailable: " + getter);
        return *(T*)IL2CPP.il2cpp_object_unbox(boxed);
    }

    public static T Unbox<T>(IntPtr boxed) where T : unmanaged
    {
        if (boxed == IntPtr.Zero) throw new InvalidOperationException("Game UI value is unavailable.");
        return *(T*)IL2CPP.il2cpp_object_unbox(boxed);
    }

    public static string GetString(IntPtr instance, string getter) =>
        IL2CPP.Il2CppStringToManaged(Call(instance, getter)) ?? string.Empty;

    public static void Set<T>(IntPtr instance, string setter, string signature, T value) where T : unmanaged =>
        Call(instance, setter, signature, value);

    public static void SetString(IntPtr instance, string setter, string value) =>
        CallObject(instance, setter, String, IL2CPP.ManagedStringToIl2Cpp(value));

    /// <summary>Retains the bounded set of names used repeatedly for native lookups.</summary>
    public static IntPtr InternString(string value)
    {
        if (LookupStrings.TryGetValue(value, out var cached))
            return cached.Pointer;
        var pointer = IL2CPP.ManagedStringToIl2Cpp(value);
        LookupStrings.Add(value, (pointer, IL2CPP.il2cpp_gchandle_new(pointer, false)));
        return pointer;
    }

    public static IntPtr ReadObject(IntPtr instance, string field) => Metadata.ReadObject(instance, field);

    public static T ReadField<T>(IntPtr instance, string field) where T : unmanaged
    {
        var info = Metadata.Field(IL2CPP.il2cpp_object_get_class(instance), field);
        if (info == IntPtr.Zero) throw new MissingFieldException(ClassName(instance), field);
        T value = default;
        IL2CPP.il2cpp_field_get_value(instance, info, &value);
        return value;
    }

    public static void WriteField<T>(IntPtr instance, string field, T value) where T : unmanaged
    {
        var info = Metadata.Field(IL2CPP.il2cpp_object_get_class(instance), field);
        if (info == IntPtr.Zero) throw new MissingFieldException(ClassName(instance), field);
        IL2CPP.il2cpp_field_set_value(instance, info, &value);
    }

    public static void WriteStringField(IntPtr instance, string field, string value)
    {
        var info = Metadata.Field(IL2CPP.il2cpp_object_get_class(instance), field);
        if (info == IntPtr.Zero) throw new MissingFieldException(ClassName(instance), field);
        var text = IL2CPP.ManagedStringToIl2Cpp(value);
        IL2CPP.il2cpp_field_set_value(instance, info, (void*)text);
    }

    public static IntPtr ReadStaticObject(IntPtr klass, string field) => Metadata.ReadStaticObject(klass, field);

    public static void Clear()
    {
        foreach (var text in LookupStrings.Values)
            IL2CPP.il2cpp_gchandle_free(text.Handle);
        LookupStrings.Clear();
        Methods.Clear();
        ClassNames.Clear();
        Metadata.Clear();
    }
}
