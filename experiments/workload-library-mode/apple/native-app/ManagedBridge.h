#import <UIKit/UIKit.h>

@interface NetLiftManagedBridge : NSObject

- (NSString *)invokeService:(uint32_t)serviceId
                     member:(uint32_t)memberId
                    payload:(NSData *)payload;

@end
