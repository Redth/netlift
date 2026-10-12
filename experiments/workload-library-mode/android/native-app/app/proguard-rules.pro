-dontwarn android.app.KeyguardManager$DeviceLockedStateListener
-dontwarn android.hardware.camera2.MultiResolutionImageReader$OnActiveOutputSurfacesListener
-dontwarn android.hardware.serial.SerialPort
-dontwarn android.hardware.serial.SerialPortListener
-dontwarn android.service.chooser.ChooserSession$StateListener
-dontwarn android.telephony.TelephonyCallback$CarrierRoamingNtnListener
-dontwarn android.telephony.satellite.NtnSignalStrength
-dontwarn android.view.View$CalledFromWrongThreadListener

-keep class dev.netlift.workloadlibrarymode.app.CallbackAcceptance {
    public static java.lang.AutoCloseable register();
}
