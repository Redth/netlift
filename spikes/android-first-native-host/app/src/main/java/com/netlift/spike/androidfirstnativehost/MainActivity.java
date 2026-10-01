package com.netlift.spike.androidfirstnativehost;

import android.app.Activity;
import android.content.pm.ApplicationInfo;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.content.pm.SigningInfo;
import android.os.Build;
import android.os.Bundle;
import android.system.Os;
import android.system.StructStat;
import android.util.Log;
import android.widget.TextView;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Locale;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

public final class MainActivity extends Activity {
    private static final String TAG = "AndroidFirstNativeHost";
    private static final String RUN_ID_PROPERTY = "netlift.firsthost.runId";
    private static final String MAPS_PROPERTY = "netlift.firsthost.baselineMaps";
    private static final String MODULE_ENTRY_PREFIX = "lib/arm64-v8a/";
    private static final Pattern SHA256_HEX = Pattern.compile("[0-9a-fA-F]{64}");

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        String runId = getIntent().getStringExtra("runId");
        if (runId == null || !runId.matches("[A-Za-z0-9._-]{1,100}")) {
            runId = "invalid-run-id";
        }
        System.setProperty(RUN_ID_PROPERTY, runId);
        emit(runId, "EVENT=JAVA_START");

        String result = verifyAndLoad(runId);
        TextView output = new TextView(this);
        output.setText(result);
        setContentView(output);
        Log.i(TAG, "runId=" + runId + " RESULT=" + result);
    }

    private String verifyAndLoad(String runId) {
        String expectedSignerInput = getIntent().getStringExtra("expectedSignerSha256");
        if (expectedSignerInput == null) {
            emit(runId, "EVENT=JAVA_REJECT reason=missing_signer_pin");
            return "FAIL reason=missing_signer_pin";
        }
        String expectedSigner = normalizeFingerprint(expectedSignerInput);
        if (expectedSigner == null) {
            emit(runId, "EVENT=JAVA_REJECT reason=malformed_signer_pin");
            return "FAIL reason=malformed_signer_pin";
        }

        try {
            PackageInfo packageInfo = getInstalledPackageInfo();
            SigningInfo signingInfo = packageInfo.signingInfo;
            if (signingInfo == null || signingInfo.hasMultipleSigners()) {
                emit(runId, "EVENT=JAVA_REJECT reason=unsupported_signing_info");
                return "FAIL reason=unsupported_signing_info";
            }

            Signature[] signers = signingInfo.getApkContentsSigners();
            if (signers == null || signers.length != 1) {
                emit(runId, "EVENT=JAVA_REJECT reason=unexpected_signer_count");
                return "FAIL reason=unexpected_signer_count";
            }
            String actualSigner = sha256(signers[0].toByteArray());
            if (!actualSigner.equalsIgnoreCase(expectedSigner)) {
                emit(runId, "EVENT=JAVA_REJECT reason=signer_mismatch actual="
                    + actualSigner + " expected=" + expectedSigner);
                return "FAIL reason=signer_mismatch";
            }

            ApplicationInfo application = packageInfo.applicationInfo;
            boolean extractNativeLibs =
                (application.flags & ApplicationInfo.FLAG_EXTRACT_NATIVE_LIBS) != 0;
            File apkFile = new File(application.sourceDir);
            File nativeLibraryDir = new File(application.nativeLibraryDir);
            emit(runId, "EVENT=JAVA_SIGNER_MATCH sha256=" + actualSigner);
            emit(runId, "EVENT=APP_LAYOUT extractNativeLibs=" + extractNativeLibs
                + " sourceDir=" + apkFile.getAbsolutePath()
                + " nativeLibraryDir=" + nativeLibraryDir.getAbsolutePath()
                + " splitSourceDirs=" + join(application.splitSourceDirs));

            String expectedHostInput = getIntent().getStringExtra("expectedHostSha256");
            String expectedDependencyInput =
                getIntent().getStringExtra("expectedDependencySha256");
            if (expectedHostInput == null || expectedDependencyInput == null) {
                emit(runId, "EVENT=JAVA_REJECT reason=missing_native_asset_pin");
                return "FAIL reason=missing_native_asset_pin";
            }
            String expectedHost = normalizeFingerprint(expectedHostInput);
            String expectedDependency = normalizeFingerprint(expectedDependencyInput);
            if (expectedHost == null || expectedDependency == null) {
                emit(runId, "EVENT=JAVA_REJECT reason=malformed_native_asset_pin");
                return "FAIL reason=malformed_native_asset_pin";
            }

            String apkHostHash = hashZipEntry(apkFile, "first_host");
            String apkDependencyHash = hashZipEntry(apkFile, "bootstrap_dependency");
            if (!apkHostHash.equalsIgnoreCase(expectedHost)) {
                emit(runId, "EVENT=JAVA_REJECT reason=first_host_digest_mismatch actual="
                    + apkHostHash + " expected=" + expectedHost);
                return "FAIL reason=first_host_digest_mismatch";
            }
            if (!apkDependencyHash.equalsIgnoreCase(expectedDependency)) {
                emit(runId, "EVENT=JAVA_REJECT reason=dependency_digest_mismatch actual="
                    + apkDependencyHash + " expected=" + expectedDependency);
                return "FAIL reason=dependency_digest_mismatch";
            }

            verifyExtractedCopy(
                runId, nativeLibraryDir, "libfirst_host.so", expectedHost);
            verifyExtractedCopy(
                runId, nativeLibraryDir, "libbootstrap_dependency.so",
                expectedDependency);
            emit(runId, "EVENT=JAVA_NATIVE_PINS_MATCH hostSha256=" + apkHostHash
                + " dependencySha256=" + apkDependencyHash);

            File mapsBefore = new File(getFilesDir(), "maps-before-native-load.txt");
            writeMapsSnapshot(mapsBefore);
            System.setProperty(MAPS_PROPERTY, mapsBefore.getAbsolutePath());
            StructStat apkStat = Os.stat(apkFile.getAbsolutePath());
            emit(runId, "EVENT=JAVA_PRELOAD_APK_STAT device=" + apkStat.st_dev
                + " inode=" + apkStat.st_ino);
            emit(runId, "EVENT=JAVA_LOADLIBRARY_BEGIN library=first_host");

            System.loadLibrary("first_host");
            int nativeDependencyValue = nativeReady();
            return nativeDependencyValue == 73
                ? "PASS reason=external_signer_and_native_pins_verified"
                : "FAIL reason=native_dependency_value_mismatch";
        } catch (PackageManager.NameNotFoundException error) {
            Log.e(TAG, "PackageManager could not find this package", error);
            return "FAIL reason=package_info_missing";
        } catch (IOException | NoSuchAlgorithmException error) {
            Log.e(TAG, "Unable to inspect signed APK assets", error);
            return "FAIL reason=asset_inspection_error exception="
                + error.getClass().getSimpleName();
        } catch (android.system.ErrnoException error) {
            Log.e(TAG, "Unable to inspect installed APK path metadata", error);
            return "FAIL reason=apk_stat_error";
        } catch (UnsatisfiedLinkError error) {
            Log.e(TAG, "The first native host could not be loaded", error);
            return "FAIL reason=first_native_load_error";
        }
    }

    private PackageInfo getInstalledPackageInfo()
        throws PackageManager.NameNotFoundException {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            return getPackageManager().getPackageInfo(
                getPackageName(),
                PackageManager.PackageInfoFlags.of(
                    PackageManager.GET_SIGNING_CERTIFICATES));
        }
        return getPackageManager().getPackageInfo(
            getPackageName(),
            PackageManager.GET_SIGNING_CERTIFICATES);
    }

    private void verifyExtractedCopy(
        String runId,
        File nativeLibraryDir,
        String libraryName,
        String expectedHash)
        throws IOException, NoSuchAlgorithmException {
        File installedCopy = new File(nativeLibraryDir, libraryName);
        if (!installedCopy.isFile()) {
            emit(runId, "EVENT=INSTALLED_LIBRARY_COPY absent name=" + libraryName);
            return;
        }

        String installedHash = sha256File(installedCopy);
        StructStat stat;
        try {
            stat = Os.stat(installedCopy.getAbsolutePath());
        } catch (android.system.ErrnoException error) {
            throw new IOException("Could not stat installed native library", error);
        }
        emit(runId, "EVENT=INSTALLED_LIBRARY_COPY name=" + libraryName
            + " sha256=" + installedHash
            + " device=" + stat.st_dev
            + " inode=" + stat.st_ino);
        if (!installedHash.equalsIgnoreCase(expectedHash)) {
            throw new IOException("Extracted native library hash differs from APK entry");
        }
    }

    private static String hashZipEntry(File apk, String libraryName)
        throws IOException, NoSuchAlgorithmException {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (ZipFile zip = new ZipFile(apk)) {
            ZipEntry entry = zip.getEntry(MODULE_ENTRY_PREFIX + "lib" + libraryName + ".so");
            if (entry == null) {
                throw new IOException("Missing native APK entry: " + libraryName);
            }
            try (InputStream input = zip.getInputStream(entry)) {
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = input.read(buffer)) != -1) {
                    digest.update(buffer, 0, read);
                }
            }
        }
        return toHex(digest.digest());
    }

    private static String sha256File(File file)
        throws IOException, NoSuchAlgorithmException {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        try (InputStream input = new FileInputStream(file)) {
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = input.read(buffer)) != -1) {
                digest.update(buffer, 0, read);
            }
        }
        return toHex(digest.digest());
    }

    private static String sha256(byte[] bytes) throws NoSuchAlgorithmException {
        return toHex(MessageDigest.getInstance("SHA-256").digest(bytes));
    }

    private static String normalizeFingerprint(String fingerprint) {
        String normalized = fingerprint.replace(":", "");
        return Pattern.matches("[0-9a-fA-F]{64}", normalized)
            ? normalized.toLowerCase(Locale.ROOT)
            : null;
    }

    private static String toHex(byte[] bytes) {
        char[] digits = "0123456789abcdef".toCharArray();
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) {
            int unsignedValue = value & 0xff;
            result.append(digits[unsignedValue >>> 4]);
            result.append(digits[unsignedValue & 0x0f]);
        }
        return result.toString();
    }

    private void writeMapsSnapshot(File destination) throws IOException {
        try (InputStream input = new FileInputStream("/proc/self/maps");
             FileOutputStream output = new FileOutputStream(destination)) {
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = input.read(buffer)) != -1) {
                output.write(buffer, 0, read);
            }
        }
    }

    private static String join(String[] values) {
        if (values == null || values.length == 0) {
            return "none";
        }
        StringBuilder result = new StringBuilder();
        for (String value : values) {
            if (result.length() != 0) {
                result.append(',');
            }
            result.append(value);
        }
        return result.toString();
    }

    private static void emit(String runId, String event) {
        Log.i(TAG, "runId=" + runId + " " + event);
    }

    private native int nativeReady();
}
