using WolfEngine.ECS;
using WolfEngine;

namespace WolfEngine.Editor;

public sealed class EditorCameraContext
{
	private readonly object _sync = new();
	private Camera _camera;
	private WorldTransform _cameraWorldTransform;
	private bool _hasValue;

	public void Publish(in Camera camera, in WorldTransform cameraWorldTransform)
	{
		lock (_sync)
		{
			_camera = camera;
			_cameraWorldTransform = cameraWorldTransform;
			_hasValue = true;
		}
	}

	public bool TryGet(out Camera camera, out WorldTransform cameraWorldTransform)
	{
		lock (_sync)
		{
			camera = _camera;
			cameraWorldTransform = _cameraWorldTransform;
			return _hasValue;
		}
	}
}
