using AntHill;
using Godot;

namespace AntHill;

public partial class AntRenderer : MultiMeshInstance2D
{
	private RenderBridge _bridge;

	public override void _Ready()
	{
		// MultiMeshInstance2D requires a Texture to render in 2D
		var img = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
		img.Fill(Colors.White);
		Texture = ImageTexture.CreateFromImage(img);

		var mm = new MultiMesh();
		mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform2D;
		mm.UseColors = true;
		mm.Mesh = CreateAntMesh();

		// Pre-allocate instance count — never change it at runtime
		mm.InstanceCount = TyphonBridge.AntCount;
		mm.VisibleInstanceCount = 0; // hidden until first frame

		// Force a huge custom AABB so Godot never culls the MultiMesh
		mm.CustomAabb = new Aabb(new Vector3(-100000, -100000, -10), new Vector3(200000, 200000, 20));

		Multimesh = mm;
	}

	public void SetBridge(RenderBridge bridge) => _bridge = bridge;

	public void UpdateFromBridge()
	{
		if (_bridge == null) return;

		var frame = _bridge.GetLatest();
		if (frame == null || frame.Count == 0) return;

		// Godot requires Buffer.Length == InstanceCount * stride (12 floats per instance).
		// Our render buffer is exactly AntCount * 12, and InstanceCount == AntCount, so lengths match.
		// VisibleInstanceCount controls how many are actually drawn.
		if (frame.Buffer.Length == Multimesh.InstanceCount * 12)
		{
			Multimesh.Buffer = frame.Buffer;
			Multimesh.VisibleInstanceCount = frame.Count;
		}
	}

	private static QuadMesh CreateAntMesh()
	{
		var mesh = new QuadMesh();
		mesh.Size = new Vector2(30, 30);
		return mesh;
	}
}
