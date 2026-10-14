package dev.netlift.gradle

import org.gradle.api.file.DirectoryProperty
import org.gradle.api.file.RegularFileProperty
import org.gradle.api.model.ObjectFactory
import org.gradle.api.provider.ListProperty
import org.gradle.api.provider.Property
import javax.inject.Inject

abstract class NetLiftExtension @Inject constructor(objects: ObjectFactory) {
    val managedProject: RegularFileProperty = objects.fileProperty()
    val contractRoots: ListProperty<String> = objects.listProperty(String::class.java)
    val nativeContractRoots: ListProperty<String> = objects.listProperty(String::class.java)
    val kotlinPackage: Property<String> = objects.property(String::class.java)
    val swiftModule: Property<String> = objects.property(String::class.java)
    val contractTargetFramework: Property<String> = objects.property(String::class.java)
    val platformViewFactory: Property<String> = objects.property(String::class.java)
    val componentTargetFramework: Property<String> = objects.property(String::class.java)
    val runtimeIdentifier: Property<String> = objects.property(String::class.java)
    val runtimeVersion: Property<String> = objects.property(String::class.java)
    val runtimeArtifactVersion: Property<String> = objects.property(String::class.java)
    val componentApplicationId: Property<String> = objects.property(String::class.java)
    val compatibilityLock: RegularFileProperty = objects.fileProperty()
    val generatedDirectory: DirectoryProperty = objects.directoryProperty()
}
