window.elevenx = {
    // ดาวน์โหลดไฟล์จาก base64 (ใช้กับ export Excel/PDF)
    downloadFile: function (filename, contentType, base64) {
        const link = document.createElement('a');
        link.href = 'data:' + contentType + ';base64,' + base64;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);
    },

    // ===== หน้า Login: parallax 3D ตามเมาส์ (เซ็ต CSS var --mx/--my) =====
    initLoginScene: function () {
        const root = document.querySelector('[data-login-scene]');
        if (!root || root.dataset.sceneReady) return;      // idempotent กัน bind ซ้ำ
        root.dataset.sceneReady = '1';
        if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

        let raf = null, tx = 0, ty = 0;
        const apply = () => {
            raf = null;
            root.style.setProperty('--mx', tx.toFixed(3));
            root.style.setProperty('--my', ty.toFixed(3));
        };
        const queue = () => { if (!raf) raf = requestAnimationFrame(apply); };
        root.addEventListener('pointermove', (e) => {
            const r = root.getBoundingClientRect();
            tx = (e.clientX - r.left) / r.width - 0.5;
            ty = (e.clientY - r.top) / r.height - 0.5;
            queue();
        });
        root.addEventListener('pointerleave', () => { tx = 0; ty = 0; queue(); });
    },

    // สลับแสดง/ซ่อนรหัสผ่าน
    togglePw: function (btn) {
        const i = document.getElementById('password');
        if (!i) return;
        const show = i.type === 'password';
        i.type = show ? 'text' : 'password';
        const on = btn.querySelector('[data-eye-on]');
        const off = btn.querySelector('[data-eye-off]');
        if (on) on.classList.toggle('hidden', !show);
        if (off) off.classList.toggle('hidden', show);
    }
};

// เผื่อหน้า Login เป็น static render — เรียกซ้ำได้ (idempotent)
document.addEventListener('DOMContentLoaded', () => window.elevenx.initLoginScene());
