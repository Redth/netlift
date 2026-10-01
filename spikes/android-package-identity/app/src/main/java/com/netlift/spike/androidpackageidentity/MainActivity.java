package com.netlift.spike.androidpackageidentity;

import android.app.Activity;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.pm.Signature;
import android.content.pm.SigningInfo;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.widget.TextView;

import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Locale;
import java.util.regex.Pattern;

public final class MainActivity extends Activity {
    private static final String TAG = "AndroidPackageIdentity";
    private static final String EXPECTED_EXTRA = "expectedSignerSha256";
    private static final Pattern SHA256_HEX = Pattern.compile("[0-9a-fA-F]{64}");

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        String runId = getIntent().getStringExtra("runId");
        if (runId == null) {
            runId = "missing-run-id";
        }
        String result = verifyInstalledSigner(
            getIntent().getStringExtra(EXPECTED_EXTRA)) + " runId=" + runId;
        TextView output = new TextView(this);
        output.setText(result);
        setContentView(output);
        Log.i(TAG, result);
    }

    private String verifyInstalledSigner(String expectedFingerprint) {
        if (expectedFingerprint == null) {
            return "RESULT=FAIL reason=missing_expected_fingerprint";
        }

        String expected = expectedFingerprint.replace(":", "");
        if (!SHA256_HEX.matcher(expected).matches()) {
            return "RESULT=FAIL reason=invalid_expected_fingerprint";
        }

        try {
            PackageInfo packageInfo = getInstalledPackageInfo();
            SigningInfo signingInfo = packageInfo.signingInfo;
            if (signingInfo == null) {
                return "RESULT=FAIL reason=missing_signing_info";
            }
            if (signingInfo.hasMultipleSigners()) {
                return "RESULT=FAIL reason=multiple_current_signers";
            }

            Signature[] currentSigners = signingInfo.getApkContentsSigners();
            if (currentSigners == null || currentSigners.length != 1) {
                return "RESULT=FAIL reason=unexpected_current_signer_count";
            }

            String actual = sha256(currentSigners[0].toByteArray());
            String outcome = actual.equalsIgnoreCase(expected) ? "PASS" : "FAIL";
            String reason = outcome.equals("PASS") ? "matched" : "signer_mismatch";
            return "RESULT=" + outcome
                + " reason=" + reason
                + " package=" + getPackageName()
                + " currentSignerSha256=" + actual
                + " expectedSignerSha256=" + expected.toLowerCase(Locale.ROOT);
        } catch (PackageManager.NameNotFoundException error) {
            return "RESULT=FAIL reason=installed_package_not_found";
        } catch (NoSuchAlgorithmException error) {
            Log.e(TAG, "Unable to verify installed package signer", error);
            return "RESULT=FAIL reason=signer_inspection_error"
                + " exception=" + error.getClass().getSimpleName();
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

    private static String sha256(byte[] certificateBytes)
        throws NoSuchAlgorithmException {
        byte[] digest = MessageDigest.getInstance("SHA-256").digest(certificateBytes);
        StringBuilder hex = new StringBuilder(digest.length * 2);
        char[] digits = "0123456789abcdef".toCharArray();
        for (byte value : digest) {
            int unsignedValue = value & 0xff;
            hex.append(digits[unsignedValue >>> 4]);
            hex.append(digits[unsignedValue & 0x0f]);
        }
        return hex.toString();
    }
}
