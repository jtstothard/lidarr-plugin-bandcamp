// A minimal .NET host for the probe; never executes the Lidarr entry point.
#include <dlfcn.h>
#include <stdio.h>
int main(int argc, char **argv) {
  void *lib = dlopen("./libhostfxr.so", RTLD_NOW | RTLD_LOCAL);
  if (!lib) { fputs(dlerror(), stderr); return 1; }
  int (*main_fn)(int, const char **, const char *, const char *, const char *) = dlsym(lib, "hostfxr_main_startupinfo");
  if (!main_fn) return 2;
  return main_fn(argc, (const char **)argv, "/host/probe-host", "/host", "/host/Probe.dll");
}
