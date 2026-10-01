import Foundation
import PackagePlugin

@main
struct NetLiftProjectionPlugin: BuildToolPlugin {
    func createBuildCommands(context: PluginContext, target: Target) throws -> [Command] {
        let fixtureSelection = context.package.directory.appending("Fixtures/contract-selection.txt")
        let selection = try String(contentsOfFile: fixtureSelection.string, encoding: .utf8)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        let baselineFixture = context.package.directory.appending("Fixtures/ProjectionFixture.g.cs")
        let changedFixture = context.package.directory.appending("Fixtures/ProjectionFixture.ContractChanged.g.cs")
        let fixture: Path
        switch selection {
        case "baseline":
            fixture = baselineFixture
        case "contract-changed":
            fixture = changedFixture
        default:
            Diagnostics.error("Unknown contract selection '\(selection)'. Expected baseline or contract-changed.")
            return []
        }
        let repositoryRoot = context.package.directory.appending("../../..")
        let projectionProject = repositoryRoot.appending("src/NetLift.Projection/NetLift.Projection.csproj")
        let projectionSource = repositoryRoot.appending("src/NetLift.Projection/Program.cs")
        let sdkPin = repositoryRoot.appending("global.json")
        let pluginWorkDirectory = context.pluginWorkDirectory
        let projectionDirectory = pluginWorkDirectory.appending("projection-output")
        let projectionBuildDirectory = pluginWorkDirectory.appending(".managed-artifacts")
        let driver = try context.tool(named: "NetLiftProjectionDriver")
        let swiftOutput = projectionDirectory.appending("NetLift.swift")
        let headerOutput = projectionDirectory.appending("netlift.h")
        let manifestOutput = projectionDirectory.appending("projection-manifest.json")
        let outputs: [Path]
        switch target.name {
        case "ProjectionClient":
            outputs = [swiftOutput, manifestOutput]
        case "ProjectionCABI":
            outputs = [
                headerOutput,
                projectionDirectory.appending("abi.c"),
                manifestOutput
            ]
        default:
            Diagnostics.error("Unexpected plugin target: \(target.name)")
            return []
        }

        Diagnostics.remark("NetLift derived outputs: \(projectionDirectory)")
        return [
            .buildCommand(
                displayName: "Project NetLift fixture into Swift and C ABI",
                executable: driver.path,
                arguments: [
                    target.name,
                    projectionProject.string,
                    repositoryRoot.string,
                    fixture.string,
                    projectionBuildDirectory.string,
                    projectionDirectory.string
                ],
                inputFiles: [
                    fixtureSelection,
                    baselineFixture,
                    changedFixture,
                    projectionProject,
                    projectionSource,
                    sdkPin
                ],
                outputFiles: outputs)
        ]
    }
}
