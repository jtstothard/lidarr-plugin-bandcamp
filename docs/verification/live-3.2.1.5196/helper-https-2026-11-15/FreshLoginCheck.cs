using Lidarr.Plugin.Bandcamp.CookiePush;
using Microsoft.Playwright;
using System.Reflection;
using System.Runtime.InteropServices;
using var stop=new CancellationTokenSource(TimeSpan.FromMinutes(10));
Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
Console.WriteLine("RUNTIME="+RuntimeInformation.FrameworkDescription);
return await CookiePushCommand.RunAsync(["--authelia","--client-name","Bandcamp","--fan-id","67857"],TextReader.Null,Console.Out,Console.Error,Environment.GetEnvironmentVariable,new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false},new PlaywrightCookieCapture(new FillFactory()),stop.Token);
sealed class FillFactory:IBrowserSessionFactory {
 readonly IBrowserSessionFactory inner=(IBrowserSessionFactory)Activator.CreateInstance(typeof(PlaywrightCookieCapture).Assembly.GetType("Lidarr.Plugin.Bandcamp.CookiePush.PlaywrightBrowserSessionFactory")!,nonPublic:true)!;
 public async Task<IBrowserSession> LaunchAsync(string path,CancellationToken ct){
  var session=await inner.LaunchAsync(path,ct);
  var context=(IBrowserContext)session.GetType().GetFields(BindingFlags.NonPublic|BindingFlags.Instance).Single(f=>f.FieldType==typeof(IBrowserContext)).GetValue(session)!;
  return new FillSession(session,context.Pages[0]);
 }
 sealed class FillSession(IBrowserSession inner,IPage page):IBrowserSession {
  public async Task OpenLoginPageAsync(CancellationToken ct){
   await inner.OpenLoginPageAsync(ct);
   var u=new Uri(page.Url);if(u.Scheme!="https"||u.Host!="bandcamp.com"||u.AbsolutePath!="/login")throw new InvalidOperationException();
   var necessary=page.GetByRole(AriaRole.Button,new(){Name="Accept necessary only",Exact=true});if(await necessary.CountAsync()>0&&await necessary.IsVisibleAsync())await necessary.ClickAsync();
   await page.Locator("#username-field").FillAsync(Environment.GetEnvironmentVariable("LOGIN_USERNAME")!);
   await page.Locator("#password-field").FillAsync(Environment.GetEnvironmentVariable("LOGIN_PASSWORD")!);
   await page.GetByRole(AriaRole.Button,new(){Name="Log in",Exact=true}).ClickAsync();
   Console.WriteLine("LOGIN_SUBMITTED_ONCE");Console.Out.Flush();
   await page.WaitForTimeoutAsync(2000);
   var captcha=page.Locator("iframe[src*=recaptcha]");var visible=false;for(int i=0;i<await captcha.CountAsync();i++)visible|=await captcha.Nth(i).IsVisibleAsync();
   Console.WriteLine("CAPTCHA_FRAME_VISIBLE="+visible);Console.WriteLine("TWO_FACTOR_VISIBLE="+await page.Locator("#twofactor-field").IsVisibleAsync());Console.Out.Flush();
  }
  public Task<string?> GetIdentityCookieAsync(CancellationToken ct)=>inner.GetIdentityCookieAsync(ct);
  public ValueTask DisposeAsync()=>inner.DisposeAsync();
 }
}
