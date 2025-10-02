using OpenTK.Mathematics;
using CAudioVisualizer.Core;

namespace CAudioVisualizer.Visualizers;

public interface IVisualizer : IDisposable
{
    string Name { get; }
    string DisplayName { get; }
    bool IsEnabled { get; set; }

    void Initialize();
    void Update(float[] waveformData, float[] fftData, double deltaTime);
    void Render(Matrix4 projection, Vector2i windowSize);
    void RenderConfigGui();
    void SetVisualizerManager(VisualizerManager manager);
}
