#import <UIKit/UIKit.h>
#include <stddef.h>
#include <stdint.h>

typedef int32_t (*NetLiftNativeServiceInvoke)(
    void *context,
    uint64_t callToken,
    uint32_t serviceId,
    uint32_t memberId,
    const uint8_t *request,
    size_t requestLength,
    uint8_t **response,
    size_t *responseLength);
typedef void (*NetLiftNativeServiceCancel)(void *context, uint64_t callToken);
typedef void (*NetLiftNativeServiceRelease)(void *context);

@interface NetLiftManagedBridge : NSObject

- (NSString *)invokeOperation:(uint64_t)operationToken
                      service:(uint32_t)serviceId
                       member:(uint32_t)memberId
                      payload:(NSData *)payload;

- (NSInteger)cancelOperation:(uint64_t)operationToken;

- (NSString *)openStreamOperation:(uint64_t)operationToken
                          service:(uint32_t)serviceId
                           member:(uint32_t)memberId
                          payload:(NSData *)payload;

- (NSString *)nextStreamOperation:(uint64_t)operationToken;

- (NSInteger)closeStreamOperation:(uint64_t)operationToken;

- (NSString *)registerNativeService:(uint32_t)serviceId
                         ownerToken:(uint64_t)ownerToken
                            context:(void *)context
                             invoke:(NetLiftNativeServiceInvoke)invoke
                             cancel:(NetLiftNativeServiceCancel)cancel
                            release:(NetLiftNativeServiceRelease)release;

- (NSInteger)revokeNativeService:(uint32_t)serviceId
                       ownerToken:(uint64_t)ownerToken;

@end
