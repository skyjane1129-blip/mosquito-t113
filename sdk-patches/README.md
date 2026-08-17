# SDK patches

这些补丁是从已成功构建 `dev-v4` 的本机 Tina SDK 中提取的、位于 `tina-overlay/` 之外的必要源码差异。

不要逐个手工执行补丁。使用：

```sh
./scripts/install-overlay.sh /路径/tina-t113 --check
./scripts/install-overlay.sh /路径/tina-t113 --apply
```

安装器会根据 [`series`](series) 在对应的 repo 子项目内检查补丁。如果补丁已经存在会跳过；如果基线不匹配则停止，不会强行覆盖未知版本。
