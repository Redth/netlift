#import <UIKit/UIKit.h>

@interface NetLiftManagedBridge : NSObject

- (NSString *)invokeOperation:(int64_t)operationToken
                      service:(uint32_t)serviceId
                       member:(uint32_t)memberId
                      payload:(NSData *)payload;

- (int32_t)cancelOperation:(int64_t)operationToken;

- (NSString *)openStreamService:(uint32_t)serviceId
                         member:(uint32_t)memberId
                        payload:(NSData *)payload;

- (NSString *)readStream:(int64_t)token
              generation:(int64_t)generation;

- (int32_t)revokeStream:(int64_t)token
             generation:(int64_t)generation;

@end
