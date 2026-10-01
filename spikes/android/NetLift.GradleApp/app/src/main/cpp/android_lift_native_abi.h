#pragma once

#include <jni.h>

#include <cstdint>

using managed_place_order_fn = int64_t (*)(const uint8_t *, int32_t, int32_t, uint64_t);

void NetLift_AbiSetJavaVm(JavaVM *vm);
void NetLift_AbiSetPlaceOrderDelegate(managed_place_order_fn place_order);
