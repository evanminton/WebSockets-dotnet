using Foundation;

namespace WebSocketTester;

// macOS 27 refuses to launch a Catalyst app that hasn't adopted the UIScene lifecycle,
// so the scene manifest in Info.plist points at this delegate.
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate
{
}
