# BPMN 部署 500 修复（documentation / extensionElements 顺序）

## 问题
V4 部署 BPMN 到 Flowable 7.2.0 时报 500：

```
cvc-complex-type.2.4.a: Invalid content was found starting with element
'{"http://www.omg.org/spec/BPMN/20100524/MODEL":documentation}'
```

## 根因
`WorkflowArtifactCompiler.Compile` 在把 slot/node 配置注入
`<pc:nodeConfigurations>` 时，用 `process.AddFirst(ext)` 创建流程级
`<extensionElements>`，把它插到了 `<documentation>` 前面。
BPMN 2.0 XSD 要求 `documentation*` 必须在 `extensionElements` 之前，
Flowable 部署时的 XSD 校验因此拒绝。

原始 BPMN 文件是合法的（documentation 在前），被编译器改写后才非法。

## 修改
文件：`Application/Services/WorkflowArtifactCompiler.cs`
位置：`WorkflowArtifactCompiler.Compile` 末尾，约第 175 行。

```diff
-        if (ext == null) { ext = new XElement(Bpmn + "extensionElements"); process.AddFirst(ext); }
+        if (ext == null)
+        {
+            ext = new XElement(Bpmn + "extensionElements");
+            // BPMN XSD requires documentation* before extensionElements; inserting
+            // extensionElements as the first child makes Flowable reject the file.
+            var documentation = process.Elements(Bpmn + "documentation").LastOrDefault();
+            if (documentation != null) documentation.AddAfterSelf(ext);
+            else process.AddFirst(ext);
+        }
```

规则：有 documentation 时插到最后一个 documentation 之后；没有时仍插到最前。

## 验证
- 修复前：5 个 standard_policy 流程部署全部 500。
- 用两个探针 BPMN 对照：documentation 在前 + pc 扩展 → Flowable 201；
  extensionElements 在前 → 500（同样的 cvc-complex-type.2.4.a）。
- 修复后：重新发布到 5012，5 个流程全部 `ACTIVE`（v1），契约校验一致，
  服务日志 0 fail/error/exception。

## 生效方式
重新 `dotnet publish` 并部署 process.dll（或整体发布目录）后重启服务。
修改未提交 git，只包含本文件。
