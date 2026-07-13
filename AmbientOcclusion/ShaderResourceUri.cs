namespace AmbientOcclusion;

internal static class ShaderResourceUri
{
    public static Uri Get(string shaderName) => new($"pack://application:,,,/AmbientOcclusion;component/Shaders/{shaderName}.cso", UriKind.Absolute);
}
