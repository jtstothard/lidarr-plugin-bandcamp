using Lidarr.Plugin.Bandcamp.CookiePush;
using System.Text.Json;
using System.Runtime.InteropServices;
var root=Path.GetTempPath();var before=Directory.GetDirectories(root,"bandcamp-cookie-push-*").ToHashSet();
using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(12));
var cancelled=false;
var factory=(IBrowserSessionFactory)Activator.CreateInstance(typeof(PlaywrightCookieCapture).Assembly.GetType("Lidarr.Plugin.Bandcamp.CookiePush.PlaywrightBrowserSessionFactory")!,nonPublic:true)!;
var observed=new ObserveFactory(factory);
try {await new PlaywrightCookieCapture(observed).CaptureAsync(cancel.Token);}
catch(OperationCanceledException){cancelled=true;}
var after=Directory.GetDirectories(root,"bandcamp-cookie-push-*").Where(p=>!before.Contains(p)).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new{runtime=RuntimeInformation.FrameworkDescription,architecture=RuntimeInformation.ProcessArchitecture.ToString(),cancelled,login_page_opened=observed.Opened,temporary_profiles_remaining=after.Length}));
return cancelled&&observed.Opened&&after.Length==0?0:1;

sealed class ObserveFactory(IBrowserSessionFactory inner):IBrowserSessionFactory {
 public bool Opened;
 public async Task<IBrowserSession> LaunchAsync(string path,CancellationToken ct)=>new ObserveSession(await inner.LaunchAsync(path,ct),this);
 sealed class ObserveSession(IBrowserSession inner,ObserveFactory owner):IBrowserSession {
  public async Task OpenLoginPageAsync(CancellationToken ct){await inner.OpenLoginPageAsync(ct);owner.Opened=true;Console.WriteLine("LOGIN_PAGE_OPENED");}
  public Task<string?> GetIdentityCookieAsync(CancellationToken ct)=>inner.GetIdentityCookieAsync(ct);
  public ValueTask DisposeAsync()=>inner.DisposeAsync();
 }
}
