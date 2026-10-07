using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace AstralPartyChatPlugin;

// HybridCLR game types do not have generated CLR wrappers in BepInEx/interop.
// Read their live IL2CPP metadata on the Unity thread instead. Cache metadata,
// never game object pointers, so reconnects cannot retain destroyed instances.
internal sealed class NativeGameData : IGameDataAccess
{
    private readonly Dictionary<string, IntPtr> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<(IntPtr Class, string Name), IntPtr> _fields = new();

    // The x64 game export writes size_t (8 bytes). The installed Interop binding
    // uses ref uint (4 bytes), so bind only this export with pointer-sized storage.
    [DllImport("GameAssembly.dll", EntryPoint = "il2cpp_domain_get_assemblies",
        ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr* GetDomainAssemblies(IntPtr domain, out nuint count);

    public unsafe IntPtr FindClass(string assemblyName, string namespaze, string name)
    {
        var key = assemblyName + ":" + namespaze + "." + name;
        if (_classes.TryGetValue(key, out var cached)) return cached;
        var assemblies = GetDomainAssemblies(IL2CPP.il2cpp_domain_get(), out var count);
        for (nuint index = 0; index < count; index++)
        {
            var image = IL2CPP.il2cpp_assembly_get_image(assemblies[index]);
            var imageName = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_image_get_name(image));
            if (!string.Equals(imageName, assemblyName, StringComparison.Ordinal)
                && !string.Equals(imageName, assemblyName + ".dll", StringComparison.Ordinal)) continue;
            var klass = IL2CPP.il2cpp_class_from_name(image, namespaze, name);
            if (klass != IntPtr.Zero) _classes[key] = klass;
            return klass;
        }
        // The game loads these assemblies after the plugin. Retry missing types.
        return IntPtr.Zero;
    }

    public IntPtr Field(IntPtr klass, string name)
    {
        if (klass == IntPtr.Zero) return IntPtr.Zero;
        var key = (klass, name);
        if (_fields.TryGetValue(key, out var cached)) return cached;
        var current = klass;
        while (current != IntPtr.Zero)
        {
            var iterator = IntPtr.Zero;
            IntPtr field;
            while ((field = IL2CPP.il2cpp_class_get_fields(current, ref iterator)) != IntPtr.Zero)
            {
                if (!string.Equals(Marshal.PtrToStringAnsi(IL2CPP.il2cpp_field_get_name(field)), name,
                        StringComparison.Ordinal)) continue;
                _fields[key] = field;
                return field;
            }
            current = IL2CPP.il2cpp_class_get_parent(current);
        }
        return IntPtr.Zero;
    }

    public IntPtr ReadObject(IntPtr instance, string fieldName)
    {
        if (instance == IntPtr.Zero) return IntPtr.Zero;
        var field = Field(IL2CPP.il2cpp_object_get_class(instance), fieldName);
        if (field == IntPtr.Zero) throw new MissingFieldException(fieldName);
        return IL2CPP.il2cpp_field_get_value_object(field, instance);
    }

    public unsafe IntPtr ReadStaticObject(IntPtr klass, string fieldName)
    {
        if (klass == IntPtr.Zero) return IntPtr.Zero;
        var field = Field(klass, fieldName);
        if (field == IntPtr.Zero) throw new MissingFieldException(fieldName);
        var fieldType = IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_field_get_type(field));
        if ((IL2CPP.il2cpp_field_get_flags(field) & 0x10) == 0
            || fieldType is not (18 or 19 or 21 or 28))
            throw new InvalidOperationException("Game singleton field has changed: " + fieldName);
        IntPtr value = IntPtr.Zero;
        IL2CPP.il2cpp_field_static_get_value(field, &value);
        return value;
    }

    public int ReadInt32(IntPtr instance, string fieldName) => UnboxInt32(ReadObject(instance, fieldName));

    public long InvokeInt64(IntPtr instance, string methodName)
    {
        var boxed = Invoke(instance, methodName);
        RequireValueType(boxed, 10);
        return Marshal.ReadInt64(IL2CPP.il2cpp_object_unbox(boxed));
    }

    public int InvokeInt32(IntPtr instance, string methodName) => UnboxInt32(Invoke(instance, methodName));

    public bool InvokeBoolean(IntPtr instance, string methodName)
    {
        var boxed = Invoke(instance, methodName);
        RequireValueType(boxed, 2);
        return Marshal.ReadByte(IL2CPP.il2cpp_object_unbox(boxed)) != 0;
    }

    public string InvokeString(IntPtr instance, string methodName)
    {
        var value = Invoke(instance, methodName);
        if (value != IntPtr.Zero) RequireValueType(value, 14);
        return value == IntPtr.Zero ? string.Empty : IL2CPP.Il2CppStringToManaged(value) ?? string.Empty;
    }

    public unsafe string InvokeString(IntPtr instance, string methodName, bool argumentValue)
    {
        if (instance == IntPtr.Zero) return string.Empty;
        var method = IL2CPP.il2cpp_class_get_method_from_name(IL2CPP.il2cpp_object_get_class(instance), methodName, 1);
        if (method == IntPtr.Zero) throw new MissingMethodException(methodName);
        if (IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_method_get_param(method, 0)) != 2)
            throw new InvalidOperationException("Game boolean getter signature has changed: " + methodName);
        IntPtr exception = IntPtr.Zero;
        byte argument = argumentValue ? (byte)1 : (byte)0;
        void* parameter = &argument;
        var result = IL2CPP.il2cpp_runtime_invoke(method, instance, &parameter, ref exception);
        if (exception != IntPtr.Zero) throw new InvalidOperationException("Game getter failed: " + methodName);
        if (result != IntPtr.Zero) RequireValueType(result, 14);
        return result == IntPtr.Zero ? string.Empty : IL2CPP.Il2CppStringToManaged(result) ?? string.Empty;
    }

    private static int UnboxInt32(IntPtr boxed)
    {
        RequireValueType(boxed, 8);
        return Marshal.ReadInt32(IL2CPP.il2cpp_object_unbox(boxed));
    }

    private static void RequireValueType(IntPtr value, int expectedType)
    {
        if (value == IntPtr.Zero) throw new InvalidOperationException("Game value is unavailable.");
        var klass = IL2CPP.il2cpp_object_get_class(value);
        var type = IL2CPP.il2cpp_class_is_enum(klass)
            ? IL2CPP.il2cpp_class_enum_basetype(klass) : IL2CPP.il2cpp_class_get_type(klass);
        if (IL2CPP.il2cpp_type_get_type(type) != expectedType)
            throw new InvalidOperationException("Game value type has changed.");
    }

    public unsafe IntPtr Invoke(IntPtr instance, string methodName)
    {
        if (instance == IntPtr.Zero) return IntPtr.Zero;
        var method = IL2CPP.il2cpp_class_get_method_from_name(IL2CPP.il2cpp_object_get_class(instance), methodName, 0);
        if (method == IntPtr.Zero) throw new MissingMethodException(methodName);
        IntPtr exception = IntPtr.Zero;
        var result = IL2CPP.il2cpp_runtime_invoke(method, instance, null, ref exception);
        if (exception != IntPtr.Zero) throw new InvalidOperationException("Game getter failed: " + methodName);
        return result;
    }

    public unsafe IntPtr Invoke(IntPtr instance, string methodName, long argumentValue)
    {
        if (instance == IntPtr.Zero) return IntPtr.Zero;
        var method = IL2CPP.il2cpp_class_get_method_from_name(IL2CPP.il2cpp_object_get_class(instance), methodName, 1);
        if (method == IntPtr.Zero) throw new MissingMethodException(methodName);
        if (IL2CPP.il2cpp_type_get_type(IL2CPP.il2cpp_method_get_param(method, 0)) != 10)
            throw new InvalidOperationException("Game Int64 getter signature has changed: " + methodName);
        IntPtr exception = IntPtr.Zero;
        long argument = argumentValue;
        void* parameter = &argument;
        var result = IL2CPP.il2cpp_runtime_invoke(method, instance, &parameter, ref exception);
        if (exception != IntPtr.Zero) throw new InvalidOperationException("Game getter failed: " + methodName);
        return result;
    }

    public void Clear()
    {
        _classes.Clear();
        _fields.Clear();
    }
}
