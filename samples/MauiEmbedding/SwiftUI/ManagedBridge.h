#import <UIKit/UIKit.h>

@interface NetLiftManagedBridge : NSObject
- (NSString *)invokeOperation:(int64_t)token service:(uint32_t)service
                       member:(uint32_t)member payload:(NSData *)payload;
- (UIView *)roundTripBorrowedView:(UIView *)view;
- (int64_t)retainPlatformView:(UIView *)view;
- (UIView * _Nullable)retainedView:(int64_t)token;
- (int32_t)releaseRetainedView:(int64_t)token;
- (int32_t)validateViewPeer:(NSObject *)view;
- (int32_t)liveRetainedViewCount;
- (UIView *)createPlatformView:(UIView *)host;
- (void)releasePlatformView:(UIView *)view;
- (int32_t)livePlatformViewCount;
- (void)collectManagedGarbage;
@end
