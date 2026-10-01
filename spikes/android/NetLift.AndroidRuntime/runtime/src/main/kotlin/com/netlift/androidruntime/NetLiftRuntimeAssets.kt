package com.netlift.androidruntime

import android.content.Context
import com.netlift.androidruntime.generated.RuntimeVariant

data class RuntimeAssetReport(
    val runtimeVersion: String,
    val variant: String,
    val managedAssemblyCount: Int,
    val hasCoreLib: Boolean
)

object NetLiftRuntimeAssets {
    fun inspect(context: Context): RuntimeAssetReport {
        val assetPath = "netlift/runtime/${RuntimeVariant.runtimeVersion}/dotnet"
        val names = context.assets.list(assetPath)
            ?: throw IllegalStateException("Generic runtime assets were not packaged at $assetPath.")
        val hasCoreLib = "System.Private.CoreLib.dll" in names
        if (!hasCoreLib) {
            throw IllegalStateException("System.Private.CoreLib.dll is missing from $assetPath.")
        }

        return RuntimeAssetReport(
            runtimeVersion = RuntimeVariant.runtimeVersion,
            variant = RuntimeVariant.name,
            managedAssemblyCount = names.count { it.endsWith(".dll") },
            hasCoreLib = hasCoreLib
        )
    }
}
