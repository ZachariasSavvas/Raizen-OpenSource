window.initColResize = function (tableId) {
    const table = document.getElementById(tableId);
    if (!table) return;

    table.style.tableLayout = 'fixed';

    table.querySelectorAll('thead th').forEach((th) => {
        // Remove any existing handle to avoid duplicates on re-render
        const old = th.querySelector('.col-resizer');
        if (old) old.remove();

        const resizer = document.createElement('div');
        resizer.className = 'col-resizer';
        th.appendChild(resizer);

        resizer.addEventListener('mousedown', (e) => {
            const startX = e.pageX;
            const startWidth = th.offsetWidth;

            document.body.style.cursor = 'col-resize';
            document.body.style.userSelect = 'none';

            const onMove = (e) => {
                const w = startWidth + (e.pageX - startX);
                if (w > 40) th.style.width = w + 'px';
            };

            const onUp = () => {
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
            };

            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
            e.preventDefault();
            e.stopPropagation();
        });
    });
};
