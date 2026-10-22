import difflib, os, io

pairs = [
    r'Views\ToolSidebarWindow.xaml.cs',
    r'Pages\SidebarLayoutPage.xaml',
    r'Pages\SidebarLayoutPage.xaml.cs',
    r'Pages\SubmitPage.xaml',
    r'Pages\SubmitPage.xaml.cs',
    r'Services\TeachingActions.cs',
    r'Pages\AutoShutdownPage.xaml.cs',
    r'Data\SidebarModule.cs',
]

old_root = r'C:\csh-win7'
new_root = r'C:\csh-up-130'
out = r'C:\csh-win7-repo\_scratch_subject'

for p in pairs:
    a = os.path.join(old_root, p)
    b = os.path.join(new_root, p)
    if not os.path.exists(a) or not os.path.exists(b):
        print('MISSING', p, os.path.exists(a), os.path.exists(b))
        continue
    with io.open(a, encoding='utf-8-sig') as f:
        al = f.read().splitlines()
    with io.open(b, encoding='utf-8-sig') as f:
        bl = f.read().splitlines()
    d = list(difflib.unified_diff(al, bl, fromfile='OLD/'+p, tofile='NEW/'+p, lineterm='', n=6))
    name = p.replace('\\', '_').replace('.', '_')
    with io.open(os.path.join(out, name + '.diff'), 'w', encoding='utf-8') as f:
        f.write('\n'.join(d))
    print(p, 'old', len(al), 'new', len(bl), 'difflines', len(d))
