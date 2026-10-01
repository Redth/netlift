package com.netlift.secondcontract

internal interface SecondContractClientObserver {
    fun onRequest(registrationToken: Long, requestToken: Long)
    fun onReplyClaimed(registrationToken: Long, requestToken: Long)
    fun onReplyCallStarted(registrationToken: Long, requestToken: Long)
    fun onReplyReturned(registrationToken: Long, requestToken: Long, failure: Exception?)
    fun onRevoke(registrationToken: Long, generation: Long)
    fun onDrainAck(registrationToken: Long, generation: Long)
    fun onRelease(registrationToken: Long)
}
