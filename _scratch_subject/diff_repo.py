import difflib, os, io

pairs = [
    (r'C:\csh-up-120\Pages\SubmitPage.xaml.cs', r'C:\csh-win7-repo\src\Pages\SubmitPage.axaml.cs', 'submit_120_repo'),
    (r'C:\csh-up-130\Pages\SubmitPage.xaml.cs', r'C:\csh-win7-repo\src\Pages\SubmitPage.axaml.cs', 'submit_130_repo'),
    (r'C:\csh-up-120\Pages\SidebarLayoutPage.xaml.cs', r'C:\csh-win7-repo\src\Pages\SidebarLayoutPage.axaml.cs', 'layout_120_repo'),
    (r'C:\csh-up-120\Views\ToolSidebarWindow.xaml.cs', r'C:\csh-win7-repo\src\Views\ToolSidebarWindow.axaml.cs', 'sidebar_120_repo'),
    (r'C:\csh-up-130\Views\ToolSidebarWindow.xaml.cs', r'C:\csh-win7-repo\src\Views\ToolSidebarWindow.axaml.cs', 'sidebar_130_repo'),
    (r'C:\csh-up-120\Services\TeachingActions.cs', r'C:\csh-win7-repo\src\Services\TeachingActions.cs', 'teach_120_repo'),
    (r'C:\csh-up-130\Services\TeachingActions.cs', r'C:\csh-win7-repo\src\Services\TeachingActions.cs', 'teach_130_repo'),
]
out = r'C:\csh-win7-repo\_scratch_subject'
for a,b,name in pairs:
    with io.open(a, encoding='utf-8-sig') as f: al=f.read().splitlines()
    with io.open(b, encoding='utf-8-sig') as f: bl=f.read().splitlines()
    d=list(difflib.unified_diff(al,bl,fromfile=a,tofile=b,lineterm='',n=4))
    with io.open(os.path.join(out,name+'.diff'),'w',encoding='utf-8') as f: f.write('\n'.join(d))
    print(name,'difflines',len(d))
