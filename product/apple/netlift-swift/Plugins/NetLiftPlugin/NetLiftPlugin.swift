import Foundation
import PackagePlugin

@main
struct NetLiftPlugin: BuildToolPlugin {
    func createBuildCommands(
        context: PluginContext,
        target: Target
    ) throws -> [Command] {
        let config = target.directory.appending("netlift.json")
        guard FileManager.default.fileExists(atPath: config.string) else {
            Diagnostics.error(
                "NetLiftPlugin requires netlift.json in target \(target.name)."
            )
            return []
        }
        let tool = try context.tool(named: "netlift-swift")
        let output = context.pluginWorkDirectory.appending("Generated")
        let work = context.pluginWorkDirectory.appending("Work")
        return [
            .prebuildCommand(
                displayName: "Generate NetLift Swift contracts for \(target.name)",
                executable: tool.path,
                arguments: [
                    "generate",
                    "--config", config.string,
                    "--package-root", context.package.directory.string,
                    "--output", output.string,
                    "--work", work.string,
                ],
                outputFilesDirectory: output
            ),
        ]
    }
}
