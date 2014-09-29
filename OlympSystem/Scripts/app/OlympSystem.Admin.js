angular.module('OlympSystem.Admin', ['ngResource', 'ngRoute']);

angular.module('OlympSystem.Admin').config(['$routeProvider', '$locationProvider',
    function ($routeProvider, $locationProvider) {
        $routeProvider
            .when('/news/', {
                templateUrl: '/admin2/partial/news',
                controller: 'NewsCtrl',
                controllerAs: 'news'
            })
            .when('/compilators/', {
                templateUrl: '/admin2/partial/compilators',
                controller: 'CompilatorsCtrl',
                controllerAs: 'compilators'
            })
            .otherwise({
                redirectTo: '/'
            });
        //$locationProvider.html5Mode(true);
    }]);

var simpleController = function (Entry) {
    this.addMode = false;

    this.items = Entry.query();

    this.toggleEdit = function (index) {
        var item = this.items[index];
        if (item.editMode) {
            this.items[index] = this.oldValue;
        } else {
            this.oldValue = angular.copy(item);
            item.editMode = !item.editMode;
        }
    };

    this.toggleAdd = function () {
        this.new = new Entry();
        this.addMode = !this.addMode;
    };

    this.save = function (item) {
        item.$update(angular.bind(this, function () { item.editMode = false; }));
    };

    this.add = function (item) {
        item.$save(angular.bind(this, function (data) { this.addMode = false; this.items.push(data); }));
    };

    this.delete = function (index) {
        var item = this.items[index];
        item.$delete(angular.bind(this, function (data) { this.items.splice(index, 1); }));
    };
};

angular.module('OlympSystem.Admin').controller('NewsCtrl', ['News', simpleController]);
angular.module('OlympSystem.Admin').controller('CompilatorsCtrl', ['Compilator', simpleController]);
        
angular.module('OlympSystem.Admin').factory('News',
    function ($resource) {
        return $resource('/api/news/:Id',
            { Id: '@Id' },
            { 'update': { method: 'PUT' } }
       );
    });

angular.module('OlympSystem.Admin').factory('Compilator',
    function ($resource) {
        return $resource('/api/compilators/:Id',
            { Id: '@Id' },
            { 'update': { method: 'PUT' } }
       );
    });