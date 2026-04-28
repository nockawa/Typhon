using AntHill;
using Godot;

namespace AntHill;

public partial class AntRenderer : Node2D
{
	private RenderBridge _bridge;
	private MultiMeshInstance2D[] _workerMeshes;
	private float[][] _workerPadBufs; // per-worker reusable padded buffers
	private MultiMeshInstance2D _overlayMesh;
	private float[] _overlayPadBuf;
	private ImageTexture _sharedTexture;
	private QuadMesh _antMesh;
	private QuadMesh _overlayMeshGeom;

	public override void _Ready()
	{
		// Circle texture: 16×16 white circle with anti-aliased edges
		const int texSize = 16;
		var img = Image.CreateEmpty(texSize, texSize, false, Image.Format.Rgba8);
		float center = (texSize - 1) * 0.5f;
		float radius = center;
		for (int y = 0; y < texSize; y++)
		{
			for (int x = 0; x < texSize; x++)
			{
				float dx = x - center;
				float dy = y - center;
				float dist = Mathf.Sqrt(dx * dx + dy * dy);
				float alpha = Mathf.Clamp(radius - dist + 0.5f, 0f, 1f);
				img.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
			}
		}
		_sharedTexture = ImageTexture.CreateFromImage(img);

		_antMesh = new QuadMesh();
		_antMesh.Size = new Vector2(15, 15);

		_overlayMeshGeom = new QuadMesh();
		_overlayMeshGeom.Size = new Vector2(15, 15);
	}

	public void SetBridge(RenderBridge bridge) => _bridge = bridge;

	public void UpdateFromBridge()
	{
		if (_bridge == null) return;

		var frame = _bridge.GetLatest();
		if (frame == null) return;

		// Lazily create worker MultiMeshInstance2D nodes
		if (_workerMeshes == null && frame.Buffers != null)
		{
			_workerMeshes = new MultiMeshInstance2D[frame.Buffers.Length];
			for (int i = 0; i < frame.Buffers.Length; i++)
			{
				var node = new MultiMeshInstance2D();
				node.Texture = _sharedTexture;
				var mm = new MultiMesh();
				mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform2D;
				mm.UseColors = true;
				mm.Mesh = _antMesh;
				mm.InstanceCount = 0;
				node.Multimesh = mm;
				node.SetClipChildrenMode(ClipChildrenMode.Disabled);
				node.ProcessMode = ProcessModeEnum.Always;
				AddChild(node);
				_workerMeshes[i] = node;
			}
		}

		if (_overlayMesh == null)
		{
			_overlayMesh = new MultiMeshInstance2D();
			_overlayMesh.Texture = _sharedTexture;
			var mm = new MultiMesh();
			mm.TransformFormat = MultiMesh.TransformFormatEnum.Transform2D;
			mm.UseColors = true;
			mm.Mesh = _overlayMeshGeom;
			mm.InstanceCount = 0;
			_overlayMesh.Multimesh = mm;
			AddChild(_overlayMesh);
			RenderingServer.CanvasItemSetCustomRect(_overlayMesh.GetCanvasItem(), true,new Rect2(-50000, -50000, 100000, 100000));
		}

		// Update worker meshes from immutable snapshots — no race
		if (_workerMeshes != null && frame.Buffers != null)
		{
			if (_workerPadBufs == null) _workerPadBufs = new float[frame.Buffers.Length][];
			for (int i = 0; i < _workerMeshes.Length && i < frame.Buffers.Length; i++)
			{
				ApplyBuffer(_workerMeshes[i], frame.Buffers[i].Data, frame.Buffers[i].Count, ref _workerPadBufs[i]);
			}
		}

		// Update overlay (food + nests)
		ApplyBuffer(_overlayMesh, frame.Overlay.Data, frame.Overlay.Count, ref _overlayPadBuf);
	}

	private static void ApplyBuffer(MultiMeshInstance2D node, float[] data, int instanceCount, ref float[] padBuf)
	{
		var mm = node.Multimesh;
		if (instanceCount == 0)
		{
			mm.VisibleInstanceCount = 0;
			return;
		}

		// Grow InstanceCount if needed, never shrink
		if (instanceCount > mm.InstanceCount)
		{
			mm.InstanceCount = instanceCount + instanceCount / 4;
		}

		int expectedLen = mm.InstanceCount * 12;
		if (data.Length == expectedLen)
		{
			mm.Buffer = data;
		}
		else
		{
			if (padBuf == null || padBuf.Length != expectedLen)
			{
				padBuf = new float[expectedLen];
			}
			System.Array.Copy(data, padBuf, System.Math.Min(instanceCount * 12, expectedLen));
			mm.Buffer = padBuf;
		}

		mm.VisibleInstanceCount = instanceCount;

		// Disable 2D culling entirely
		//RenderingServer.CanvasItemSetCustomRect(node.GetCanvasItem(), true, new Rect2(-50000, -50000, 100000, 100000));
		node.QueueRedraw();
	}

	private static QuadMesh CreateAntMesh()
	{
		var mesh = new QuadMesh();
		mesh.Size = new Vector2(15, 15);
		return mesh;
	}
}
