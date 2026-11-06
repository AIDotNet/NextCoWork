/**
 * 逻辑侧 —— 这一版的演示编辑器不需要它做事。
 *
 * 编辑器是声明式的:清单里的 `customEditors[].documentEngine` 让宿主把视图接到文档会话上,
 * 画布、输入、缩略图、放映、保存都走视图侧(`view/editor.tsx`)。清单的 `main` 必须存在,宿主在
 * `onCustomEditor:` 事件到达时唤醒它 —— 所以这里是有意为空的 activate。
 */
export function activate(): void {
  // 有意为空,见文件头。
}

export function deactivate(): void {
  // 没有要释放的东西:画布的会话由宿主随 Tab 收掉。
}
