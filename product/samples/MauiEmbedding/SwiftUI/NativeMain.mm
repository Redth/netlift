#import <UIKit/UIKit.h>

extern "C" int netlift_workload_apple_start(int argc, char **argv);

extern "C" int netlift_native_ui_main()
{
    char *arguments[] = { nullptr };
    return UIApplicationMain(0, arguments, nil, @"NetLiftNativeAppDelegate");
}

int main(int argc, char **argv)
{
    return netlift_workload_apple_start(argc, argv);
}
