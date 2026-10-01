package com.netlift.spike.androidsplitfirsthost;

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
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.regex.Pattern;
import java.util.zip.ZipEntry;
import java.util.zip.ZipFile;

public final class MainActivity extends Activity {
    private static final String TAG = "AndroidSplitFirstHost";
    private static final String RUN_ID_PROPERTY = "netlift.splitfirsthost.runId";
    private static final String MAPS_PROPERTY = "netlift.splitfirsthost.baselineMaps";
    private static final String ABI = "arm64-v8a";
    private static final String LIB_PREFIX = "lib/";
    private static final String HOST = "libfirst_host.so";
    private static final String DEPENDENCY = "libbootstrap_dependency.so";
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
        String expectedSigner = normalizeFingerprint(expectedSignerInput);
        if (expectedSignerInput == null) {
            emit(runId, "EVENT=JAVA_REJECT reason=missing_signer_pin");
            return "FAIL reason=missing_signer_pin";
        }
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
            Signature[] currentSigners = signingInfo.getApkContentsSigners();
            if (currentSigners == null || currentSigners.length != 1) {
                emit(runId, "EVENT=JAVA_REJECT reason=unexpected_signer_count");
                return "FAIL reason=unexpected_signer_count";
            }
            String actualSigner = sha256(currentSigners[0].toByteArray());
            emit(runId, "EVENT=INSTALLED_SIGNER_SET count=1 sha256=" + actualSigner);
            if (!actualSigner.equalsIgnoreCase(expectedSigner)) {
                emit(runId, "EVENT=JAVA_REJECT reason=signer_mismatch actual="
                    + actualSigner + " expected=" + expectedSigner);
                return "FAIL reason=signer_mismatch";
            }

            ApplicationInfo application = packageInfo.applicationInfo;
            boolean extractNativeLibs =
                (application.flags & ApplicationInfo.FLAG_EXTRACT_NATIVE_LIBS) != 0;
            File nativeLibraryDir = new File(application.nativeLibraryDir);
            emit(runId, "EVENT=JAVA_SIGNER_MATCH sha256=" + actualSigner);
            emit(runId, "EVENT=APP_LAYOUT extractNativeLibs=" + extractNativeLibs
                + " sourceDir=" + application.sourceDir
                + " nativeLibraryDir=" + nativeLibraryDir.getAbsolutePath()
                + " splitSourceDirs=" + join(application.splitSourceDirs));

            ArrayList<File> apkFiles = new ArrayList<>();
            apkFiles.add(new File(application.sourceDir));
            if (application.splitSourceDirs != null) {
                for (String path : application.splitSourceDirs) {
                    apkFiles.add(new File(path));
                }
            }
            for (int i = 0; i < apkFiles.size(); i++) {
                emitFileStat(runId, "PACKAGE_APK_STAT", i, apkFiles.get(i));
            }
            if (nativeLibraryDir.exists()) {
                emitFileStat(runId, "NATIVE_LIBRARY_DIR_STAT", -1, nativeLibraryDir);
            } else {
                emit(runId, "EVENT=NATIVE_LIBRARY_DIR_STAT absent path="
                    + nativeLibraryDir.getAbsolutePath());
            }

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

            Map<String, ModuleEntry> selectedAbiEntries = new HashMap<>();
            for (int i = 0; i < apkFiles.size(); i++) {
                inventoryApk(runId, i, apkFiles.get(i), selectedAbiEntries);
            }
            ModuleEntry hostEntry = selectedAbiEntries.get(HOST);
            ModuleEntry dependencyEntry = selectedAbiEntries.get(DEPENDENCY);
            if (hostEntry == null || dependencyEntry == null) {
                emit(runId, "EVENT=JAVA_REJECT reason=missing_native_entry"
                    + " host=" + (hostEntry != null)
                    + " dependency=" + (dependencyEntry != null));
                return "FAIL reason=missing_native_entry";
            }
            if (!hostEntry.sha256.equalsIgnoreCase(expectedHost)) {
                emit(runId, "EVENT=JAVA_REJECT reason=first_host_digest_mismatch"
                    + " actual=" + hostEntry.sha256 + " expected=" + expectedHost);
                return "FAIL reason=first_host_digest_mismatch";
            }
            if (!dependencyEntry.sha256.equalsIgnoreCase(expectedDependency)) {
                emit(runId, "EVENT=JAVA_REJECT reason=dependency_digest_mismatch"
                    + " actual=" + dependencyEntry.sha256
                    + " expected=" + expectedDependency);
                return "FAIL reason=dependency_digest_mismatch";
            }

            verifyExtractedCopy(
                runId, nativeLibraryDir, HOST, expectedHost, extractNativeLibs);
            verifyExtractedCopy(
                runId, nativeLibraryDir, DEPENDENCY, expectedDependency, extractNativeLibs);
            emit(runId, "EVENT=JAVA_NATIVE_PINS_MATCH hostSha256=" + hostEntry.sha256
                + " hostApk=" + hostEntry.apk.getAbsolutePath()
                + " dependencySha256=" + dependencyEntry.sha256
                + " dependencyApk=" + dependencyEntry.apk.getAbsolutePath());

            File mapsBefore = new File(getFilesDir(), "maps-before-native-load.txt");
            writeMapsSnapshot(mapsBefore);
            System.setProperty(MAPS_PROPERTY, mapsBefore.getAbsolutePath());
            emit(runId, "EVENT=JAVA_LOADLIBRARY_BEGIN library=first_host"
                + " apk=" + hostEntry.apk.getAbsolutePath()
                + " entry=" + hostEntry.entryName);
            System.loadLibrary("first_host");
            int result = nativeReady();
            return result == 73
                ? "PASS reason=external_signer_and_split_native_pins_verified"
                : "FAIL reason=native_dependency_value_mismatch";
        } catch (PackageManager.NameNotFoundException error) {
            Log.e(TAG, "PackageManager could not find this package", error);
            return "FAIL reason=package_info_missing";
        } catch (IOException | NoSuchAlgorithmException error) {
            Log.e(TAG, "Unable to inspect installed split APK assets", error);
            return "FAIL reason=asset_inspection_error exception="
                + error.getClass().getSimpleName();
        } catch (android.system.ErrnoException error) {
            Log.e(TAG, "Unable to inspect installed code path metadata", error);
            return "FAIL reason=path_stat_error";
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

    private void inventoryApk(
        String runId,
        int apkIndex,
        File apk,
        Map<String, ModuleEntry> selectedAbiEntries)
        throws IOException, NoSuchAlgorithmException {
        try (ZipFile zip = new ZipFile(apk)) {
            Enumeration<? extends ZipEntry> entries = zip.entries();
            while (entries.hasMoreElements()) {
                ZipEntry entry = entries.nextElement();
                String name = entry.getName();
                if (entry.isDirectory() || !name.startsWith(LIB_PREFIX)
                    || !name.endsWith(".so")) {
                    continue;
                }
                String hash;
                try (InputStream input = zip.getInputStream(entry)) {
                    hash = sha256(input);
                }
                emit(runId, "EVENT=APK_NATIVE_ENTRY apkIndex=" + apkIndex
                    + " apk=" + apk.getAbsolutePath()
                    + " entry=" + name + " sha256=" + hash);
                String abiPrefix = LIB_PREFIX + ABI + "/";
                if (name.startsWith(abiPrefix)) {
                    String moduleName = name.substring(abiPrefix.length());
                    ModuleEntry previous = selectedAbiEntries.putIfAbsent(
                        moduleName, new ModuleEntry(apk, name, hash));
                    if (previous != null) {
                        emit(runId, "EVENT=JAVA_REJECT reason=duplicate_native_entry"
                            + " entry=" + name);
                        throw new IOException("Duplicate ABI module " + name);
                    }
                }
            }
        }
    }

    private void verifyExtractedCopy(
        String runId,
        File nativeLibraryDir,
        String name,
        String expectedHash,
        boolean extractNativeLibs)
        throws IOException, NoSuchAlgorithmException, android.system.ErrnoException {
        File installedCopy = new File(nativeLibraryDir, name);
        if (!installedCopy.isFile()) {
            emit(runId, "EVENT=INSTALLED_LIBRARY_COPY absent name=" + name);
            if (extractNativeLibs) {
                throw new IOException("Expected extracted library is missing: " + name);
            }
            return;
        }
        String installedHash;
        try (InputStream input = new FileInputStream(installedCopy)) {
            installedHash = sha256(input);
        }
        StructStat stat = Os.stat(installedCopy.getAbsolutePath());
        emit(runId, "EVENT=INSTALLED_LIBRARY_COPY name=" + name
            + " sha256=" + installedHash + " device=" + stat.st_dev
            + " inode=" + stat.st_ino + " uid=" + stat.st_uid
            + " gid=" + stat.st_gid + " mode=" + Integer.toOctalString(stat.st_mode & 07777));
        if (!installedHash.equalsIgnoreCase(expectedHash)) {
            throw new IOException("Extracted library hash differs from signed APK entry: " + name);
        }
    }

    private static void emitFileStat(String runId, String event, int index, File file)
        throws android.system.ErrnoException {
        StructStat stat = Os.stat(file.getAbsolutePath());
        emit(runId, "EVENT=" + event + " index=" + index
            + " path=" + file.getAbsolutePath()
            + " device=" + stat.st_dev + " inode=" + stat.st_ino
            + " uid=" + stat.st_uid + " gid=" + stat.st_gid
            + " mode=" + Integer.toOctalString(stat.st_mode & 07777));
    }

    private static String sha256(InputStream input)
        throws IOException, NoSuchAlgorithmException {
        MessageDigest digest = MessageDigest.getInstance("SHA-256");
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = input.read(buffer)) != -1) {
            digest.update(buffer, 0, read);
        }
        return toHex(digest.digest());
    }

    private static String sha256(byte[] bytes) throws NoSuchAlgorithmException {
        return toHex(MessageDigest.getInstance("SHA-256").digest(bytes));
    }

    private static String normalizeFingerprint(String value) {
        if (value == null) {
            return null;
        }
        String normalized = value.replace(":", "");
        return SHA256_HEX.matcher(normalized).matches()
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
            if (result.length() > 0) {
                result.append(',');
            }
            result.append(value);
        }
        return result.toString();
    }

    private static void emit(String runId, String event) {
        Log.i(TAG, "runId=" + runId + " " + event);
    }

    private static final class ModuleEntry {
        final File apk;
        final String entryName;
        final String sha256;

        ModuleEntry(File apk, String entryName, String sha256) {
            this.apk = apk;
            this.entryName = entryName;
            this.sha256 = sha256;
        }
    }

    private native int nativeReady();
}
