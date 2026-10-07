using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace UnityEngine
{
    [Flags]
    public enum HideFlags { None = 0, DontUnloadUnusedAsset = 32 }

    // Models Unity's destroyed-native-object/null comparison and unused-asset
    // cleanup. Tests compile the production asset cache and request code.
    public class Object
    {
        private static readonly List<Object> All = new();
        public Object() => All.Add(this);
        public string name { get; set; } = string.Empty;
        public HideFlags hideFlags { get; set; }
        public bool IsDestroyed { get; private set; }
        public static void Destroy(Object target) => target.IsDestroyed = true;
        public static void UnloadUnusedAssets()
        {
            foreach (var item in All.Where(item => !item.IsDestroyed &&
                (item.hideFlags & HideFlags.DontUnloadUnusedAsset) == 0).ToArray()) Destroy(item);
        }
        public static bool operator ==(Object? left, Object? right)
        {
            if (ReferenceEquals(left, null)) return ReferenceEquals(right, null) || right.IsDestroyed;
            if (ReferenceEquals(right, null)) return left.IsDestroyed;
            return ReferenceEquals(left, right);
        }
        public static bool operator !=(Object? left, Object? right) => !(left == right);
        public override bool Equals(object? other) => other is Object value && this == value;
        public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
    }

    public class Texture : Object { }
    public enum TextureFormat { RGBA32 }
    public enum TextureWrapMode { Clamp }
    public enum FilterMode { Bilinear }
    public sealed class Texture2D : Texture
    {
        public Texture2D(int width, int height, TextureFormat format, bool mipmaps)
        { this.width = width; this.height = height; }
        public int width { get; set; }
        public int height { get; set; }
        public TextureWrapMode wrapMode { get; set; }
        public FilterMode filterMode { get; set; }
    }
    public readonly record struct Vector2(float x, float y);
    public readonly record struct Rect(float x, float y, float width, float height);
    public sealed class Sprite : Object
    {
        public Texture2D texture { get; init; } = null!;
        public static Sprite Create(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit) => new() { texture = texture };
    }
    public sealed class Font : Object
    {
        public static Font CreateDynamicFontFromOSFont(string name, int size) => new() { name = name };
    }
    public static class Resources
    {
        public static T[] FindObjectsOfTypeAll<T>() => Array.Empty<T>();
        public static T GetBuiltinResource<T>(string name) where T : Object, new() => new() { name = name };
    }
    public static class Time { public static float realtimeSinceStartup => 0f; }
    public static class ImageConversion
    {
        public static bool DecodeSucceeds { get; set; } = true;
        public static bool LoadImage(Texture2D texture, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte> bytes, bool nonReadable)
        {
            if (!DecodeSucceeds) return false;
            texture.width = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Value.AsSpan(16, 4));
            texture.height = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.Value.AsSpan(20, 4));
            return true;
        }
    }
}

namespace UnityEngine.UI
{
    public sealed class Text : UnityEngine.Object
    {
        public UnityEngine.Font? font { get; set; }
        public void SetVerticesDirty() { }
        public void SetLayoutDirty() { }
    }
}

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public sealed class Il2CppStructArray<T>
    {
        public Il2CppStructArray(T[] value) => Value = value;
        public T[] Value { get; }
    }
}
