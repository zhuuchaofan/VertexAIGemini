/**
 * ChatInput 键盘事件处理模块
 * - Enter: 发送消息
 * - Shift+Enter: 浏览器原生换行
 */
window.chatInputHandler = {
    init: function(textareaRef, dotnetRef) {
        textareaRef.addEventListener('keydown', function(e) {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                dotnetRef.invokeMethodAsync('SendMessage');
            }
            // Shift+Enter: 不干预，让浏览器自然换行
        });
    }
};
