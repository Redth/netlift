package com.netlift.spike.androidsignerfdchain;

import android.app.Activity;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.content.pm.SigningInfo;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.widget.TextView;

import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Locale;
import java.util.regex.Pattern;

public final class MainActivity extends Activity {
    private static final String TAG = "AndroidSignerFdChain";
    private static final String RUN_ID_PROPERTY = "netlift.android.signerfd.runId";
    private static final Pattern SHA256_HEX = Pattern.compile("[0-9a-fA-F]{64}");

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        String runId = getIntent().getStringExtra("runId");
        if (runId == null || !runId.matches("[A-Za-z0-9._-]{1,100}")) {
            runId = "invalid-run-id";
        }
        System.setProperty(RUN_ID_PROPERTY, runId);
        emit(runId, "EVENT=JAVA_VERIFY_START");

        String result = verifyThenLoad(runId);
        TextView output = new TextView(this);
        output.setText(result);
        setContentView(output);
        Log.i(TAG, "runId=" + runId + " RESULT=" + result);
    }

    private String verifyThenLoad(String runId) {
        String signerPinInput = getIntent().getStringExtra("expectedSignerSha256");
        if (signerPinInput == null) {
            emit(runId, "EVENT=JAVA_SIGNER_REJECT reason=missing_signer_pin");
            return "FAIL reason=missing_signer_pin";
        }
        String expectedSigner = normalizeFingerprint(signerPinInput);
        if (expectedSigner == null) {
            emit(runId, "EVENT=JAVA_SIGNER_REJECT reason=malformed_signer_pin");
            return "FAIL reason=malformed_signer_pin";
        }

        try {
            PackageInfo packageInfo = getInstalledPackageInfo();
            SigningInfo signingInfo = packageInfo.signingInfo;
            if (signingInfo == null || signingInfo.hasMultipleSigners()) {
                emit(runId, "EVENT=JAVA_SIGNER_REJECT reason=unsupported_signing_info");
                return "FAIL reason=unsupported_signing_info";
            }

            Signature[] signers = signingInfo.getApkContentsSigners();
            if (signers == null || signers.length != 1) {
                emit(runId, "EVENT=JAVA_SIGNER_REJECT reason=unexpected_signer_count");
                return "FAIL reason=unexpected_signer_count";
            }

            String actualSigner = sha256(signers[0].toByteArray());
            if (!actualSigner.equalsIgnoreCase(expectedSigner)) {
                emit(runId, "EVENT=JAVA_SIGNER_REJECT reason=signer_mismatch actual="
                    + actualSigner + " expected=" + expectedSigner);
                return "FAIL reason=signer_mismatch";
            }
            emit(runId, "EVENT=JAVA_SIGNER_MATCH sha256=" + actualSigner);

            String assetPinInput = getIntent().getStringExtra("expectedModuleSha256");
            if (assetPinInput == null) {
                emit(runId, "EVENT=JAVA_ASSET_REJECT reason=missing_module_pin");
                return "FAIL reason=missing_module_pin";
            }
            String expectedAsset = normalizeFingerprint(assetPinInput);
            if (expectedAsset == null) {
                emit(runId, "EVENT=JAVA_ASSET_REJECT reason=malformed_module_pin");
                return "FAIL reason=malformed_module_pin";
            }

            File packagedModule = new File(
                getApplicationInfo().nativeLibraryDir, "libfixture_a.so");
            String actualAsset = sha256File(packagedModule);
            if (!actualAsset.equalsIgnoreCase(expectedAsset)) {
                emit(runId, "EVENT=JAVA_ASSET_HASH_MISMATCH actual="
                    + actualAsset + " expected=" + expectedAsset);
                return "FAIL reason=module_digest_mismatch";
            }
            emit(runId, "EVENT=JAVA_ASSET_HASH_MATCH sha256=" + actualAsset);

            emit(runId, "EVENT=JAVA_NATIVE_HOST_LOAD_BEGIN");
            System.loadLibrary("trust_host");
            String nativeResult = runNativeChain(
                getApplicationInfo().nativeLibraryDir,
                getFilesDir().getAbsolutePath(),
                expectedAsset,
                runId);
            emit(runId, "EVENT=JAVA_NATIVE_HOST_RETURN result=" + nativeResult);
            return nativeResult.startsWith("NATIVE_CHAIN=PASS")
                ? "PASS reason=signer_and_fd_chain_verified"
                : "FAIL reason=native_fd_chain_failed";
        } catch (PackageManager.NameNotFoundException error) {
            Log.e(TAG, "PackageManager could not find the running package", error);
            return "FAIL reason=package_info_missing";
        } catch (IOException | NoSuchAlgorithmException error) {
            Log.e(TAG, "Unable to inspect installed package or module bytes", error);
            return "FAIL reason=inspection_error exception="
                + error.getClass().getSimpleName();
        } catch (UnsatisfiedLinkError error) {
            Log.e(TAG, "Unable to load the post-verification native host", error);
            return "FAIL reason=native_host_load_error";
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

    private static String normalizeFingerprint(String fingerprint) {
        if (fingerprint == null) {
            return null;
        }
        String normalized = fingerprint.replace(":", "");
        return SHA256_HEX.matcher(normalized).matches()
            ? normalized.toLowerCase(Locale.ROOT)
            : null;
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

    private static void emit(String runId, String event) {
        Log.i(TAG, "runId=" + runId + " " + event);
    }

    private native String runNativeChain(
        String nativeLibraryDir,
        String filesDir,
        String expectedModuleSha256,
        String runId);
}
