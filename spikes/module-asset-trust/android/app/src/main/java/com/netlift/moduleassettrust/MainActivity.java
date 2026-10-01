package com.netlift.moduleassettrust;

import android.app.Activity;
import android.os.Bundle;
import android.util.Log;
import android.widget.TextView;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.security.MessageDigest;

public final class MainActivity extends Activity {
    private static final String TAG = "ModuleAssetTrustProbe";

    static {
        System.loadLibrary("trust_host");
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        String runId = getIntent().getStringExtra("runId");
        if (runId == null) {
            runId = "missing-run-id";
        }
        String result;
        try {
            File packagedModule = new File(
                getApplicationInfo().nativeLibraryDir,
                "libfixture_v1.so");
            String packagedHash = sha256File(packagedModule);
            result = runTrustTest(
                getApplicationInfo().nativeLibraryDir,
                getFilesDir().getAbsolutePath(),
                packagedHash,
                runId);
        } catch (Exception error) {
            result = "RESULT:" + runId + ":EXCEPTION:" +
                error.getClass().getSimpleName() + ":" + error.getMessage();
        }

        TextView output = new TextView(this);
        output.setText(result);
        setContentView(output);
        Log.i(TAG, result);
    }

    private static String sha256File(File file) throws Exception {
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

    private static String sha256Bytes(byte[] bytes) {
        try {
            return toHex(MessageDigest.getInstance("SHA-256").digest(bytes));
        } catch (Exception error) {
            return "";
        }
    }

    private static String toHex(byte[] bytes) {
        StringBuilder result = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) {
            result.append(String.format("%02x", value & 0xff));
        }
        return result.toString();
    }

    private native String runTrustTest(
        String nativeLibraryDir,
        String filesDir,
        String expectedModuleHash,
        String runId);
}
