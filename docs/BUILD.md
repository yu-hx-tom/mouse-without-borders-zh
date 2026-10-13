# 开发与检查

需要 Windows、PowerShell 7 和 .NET Framework 自带的 C# 编译器。Mono.Cecil 随仓库提供，许可见第三方说明。

## 构建扩展

```powershell
pwsh -NoProfile -File .\BuildExtensions.ps1
```

这个命令编译我们自己的三个扩展 DLL；首次构建会生成本地程序集密钥，密钥不进入 Git。

之后从你合法安装的微软独立版准备宿主。新生成的密钥请搭配**未修改的微软原版**文件，不能沿用引用旧密钥的已修改主程序：

```powershell
pwsh -NoProfile -File .\Install.ps1 -Mode PrepareOnly -OriginalDirectory "C:\路径\到\微软原版目录"
```

该目录应包含原版主程序、helper、服务程序及原版依赖；原版主程序与 helper 有固定哈希校验。PrepareOnly 只构建，不写入安装目录。生成文件仍适用原版许可，不要将它们公开上传。

## 检查

```powershell
pwsh -NoProfile -File .\analysis\v9\test-native-runtime.ps1
pwsh -NoProfile -File .\analysis\v9\test-native-drag-state.ps1
pwsh -NoProfile -File .\analysis\v9\run-codex-replay.ps1
pwsh -NoProfile -File .\analysis\v9\test-helper-release.ps1
pwsh -NoProfile -File .\analysis\v9\test-native-files.ps1
pwsh -NoProfile -File .\analysis\test-drop-target.ps1
pwsh -NoProfile -File .\analysis\test-drag-preview.ps1
```

回放检查使用替身平台；目标识别及预览检查使用合成窗口和目录。它们不能代替实际两台电脑的文件拖放、键盘输入和 Win11 验收。

公开 Release 仅收录新增扩展、公开源码、Mono.Cecil 和使用文档。请排除 original/、生成的宿主 EXE、密钥、设备备份、日志和本地诊断记录。
