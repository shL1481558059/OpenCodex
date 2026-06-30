import { copyFileSync, chmodSync, cpSync, mkdirSync, rmSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

const rootDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const target = `${process.platform}-${process.arch}`;

const targets = {
  "darwin-arm64": {
    rid: "osx-arm64",
    triple: "aarch64-apple-darwin",
    executable: "OpenCodex.Api",
    sidecarExt: ""
  },
  "darwin-x64": {
    rid: "osx-x64",
    triple: "x86_64-apple-darwin",
    executable: "OpenCodex.Api",
    sidecarExt: ""
  },
  "linux-arm64": {
    rid: "linux-arm64",
    triple: "aarch64-unknown-linux-gnu",
    executable: "OpenCodex.Api",
    sidecarExt: ""
  },
  "linux-x64": {
    rid: "linux-x64",
    triple: "x86_64-unknown-linux-gnu",
    executable: "OpenCodex.Api",
    sidecarExt: ""
  },
  "win32-arm64": {
    rid: "win-arm64",
    triple: "aarch64-pc-windows-msvc",
    executable: "OpenCodex.Api.exe",
    sidecarExt: ".exe"
  },
  "win32-x64": {
    rid: "win-x64",
    triple: "x86_64-pc-windows-msvc",
    executable: "OpenCodex.Api.exe",
    sidecarExt: ".exe"
  }
};

const selected = targets[target];
if (!selected) {
  throw new Error(`Unsupported desktop target: ${target}`);
}

const binariesDir = path.join(rootDir, "src-tauri", "binaries");
const resourcesDir = path.join(rootDir, "src-tauri", "resources");
const publishDir = path.join(binariesDir, "publish", selected.rid);
const projectPath = path.join(
  rootDir,
  "opencodex_proxy",
  "src",
  "Presentation",
  "OpenCodex.Api",
  "OpenCodex.Api.csproj"
);
const sidecarPath = path.join(
  binariesDir,
  `opencodex-api-${selected.triple}${selected.sidecarExt}`
);

run("npm", ["--prefix", "frontend", "run", "build"]);
rmSync(path.join(resourcesDir, "wwwroot"), { recursive: true, force: true });
mkdirSync(path.join(resourcesDir, "wwwroot"), { recursive: true });
cpSync(
  path.join(rootDir, "frontend", "dist", "admin"),
  path.join(resourcesDir, "wwwroot", "admin"),
  { recursive: true }
);

rmSync(publishDir, { recursive: true, force: true });
mkdirSync(publishDir, { recursive: true });
run("dotnet", [
  "publish",
  projectPath,
  "--configuration",
  "Release",
  "--runtime",
  selected.rid,
  "--self-contained",
  "true",
  "-p:PublishSingleFile=true",
  "-p:IncludeNativeLibrariesForSelfExtract=true",
  "-p:DebugType=None",
  "-p:DebugSymbols=false",
  "--output",
  publishDir
]);

copyFileSync(path.join(publishDir, selected.executable), sidecarPath);
if (process.platform !== "win32") {
  chmodSync(sidecarPath, 0o755);
}

console.log(`Prepared Tauri sidecar: ${path.relative(rootDir, sidecarPath)}`);

function run(command, args) {
  const result = spawnSync(command, args, {
    cwd: rootDir,
    stdio: "inherit",
    shell: process.platform === "win32"
  });
  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(" ")} failed with exit code ${result.status}`);
  }
}
