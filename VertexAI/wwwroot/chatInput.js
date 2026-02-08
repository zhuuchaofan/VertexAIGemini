/**
 * ChatInput 键盘事件、粘贴和拖拽处理模块
 * - Enter: 发送消息
 * - Shift+Enter: 浏览器原生换行
 * - Ctrl/Cmd+V: 粘贴图片
 * - 拖拽: 拖拽图片到输入区域
 */
window.chatInputHandler = {
    init: function(textareaRef, dotnetRef) {
        const container = textareaRef.closest('footer');

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
                    processImageFile(file, dotnetRef);
                    break;
                }
            }
        });

        // 拖拽事件 - 作用于整个 footer 区域
        if (container) {
            container.addEventListener('dragover', function(e) {
                e.preventDefault();
                e.stopPropagation();
                container.classList.add('ring-2', 'ring-sky-400', 'ring-inset');
            });

            container.addEventListener('dragleave', function(e) {
                e.preventDefault();
                e.stopPropagation();
                container.classList.remove('ring-2', 'ring-sky-400', 'ring-inset');
            });

            container.addEventListener('drop', async function(e) {
                e.preventDefault();
                e.stopPropagation();
                container.classList.remove('ring-2', 'ring-sky-400', 'ring-inset');

                const files = e.dataTransfer?.files;
                if (!files) return;

                for (const file of files) {
                    if (file.type.startsWith('image/')) {
                        processImageFile(file, dotnetRef);
                    }
                }
            });
        }
    }
};

// 通用图片处理函数
function processImageFile(file, dotnetRef) {
    // 验证大小 (5MB)
    if (file.size > 5 * 1024 * 1024) {
        dotnetRef.invokeMethodAsync('OnPasteError', '图片大小不能超过 5MB');
        return;
    }

    // 转换为 Base64
    const reader = new FileReader();
    reader.onload = function() {
        const base64 = reader.result.split(',')[1];
        dotnetRef.invokeMethodAsync('OnImagePasted', base64, file.type, file.name || 'dropped-image');
    };
    reader.onerror = function() {
        dotnetRef.invokeMethodAsync('OnPasteError', '读取图片失败');
    };
    reader.readAsDataURL(file);
}
