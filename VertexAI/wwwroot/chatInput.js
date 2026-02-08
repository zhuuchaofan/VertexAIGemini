/**
 * ChatInput 键盘事件和粘贴处理模块
 * - Enter: 发送消息
 * - Shift+Enter: 浏览器原生换行
 * - Ctrl/Cmd+V: 粘贴图片
 */
window.chatInputHandler = {
    init: function(textareaRef, dotnetRef) {
        // 键盘事件
        textareaRef.addEventListener('keydown', function(e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('SendMessage');
            }
        });

        // 粘贴事件
        textareaRef.addEventListener('paste', async function(e) {
            const items = e.clipboardData?.items;
            if (!items) return;

            for (const item of items) {
                if (item.type.startsWith('image/')) {
                    e.preventDefault();
                    const file = item.getAsFile();
                    if (!file) continue;

                    // 验证大小 (5MB)
                    if (file.size > 5 * 1024 * 1024) {
                        dotnetRef.invokeMethodAsync('OnPasteError', '图片大小不能超过 5MB');
                        return;
                    }

                    // 转换为 Base64
                    const reader = new FileReader();
                    reader.onload = function() {
                        const base64 = reader.result.split(',')[1];
                        dotnetRef.invokeMethodAsync('OnImagePasted', base64, file.type, file.name || 'pasted-image');
                    };
                    reader.onerror = function() {
                        dotnetRef.invokeMethodAsync('OnPasteError', '读取图片失败');
                    };
                    reader.readAsDataURL(file);
                    break; // 只处理第一张图片
                }
            }
        });
    }
};
