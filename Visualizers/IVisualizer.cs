using OpenTK.Mathematics;

namespace CAudioVisualizer.Visualizers;

public interface IVisualizer : IDisposable
{
    string Name { get; }
    string DisplayName { get; }
    bool IsEnabled { get; set; }

    void Initialize();
    void Update(float[] waveformData, double deltaTime);
    void Render(Matrix4 projection, Vector2i windowSize);
    void RenderConfigGui();
}
